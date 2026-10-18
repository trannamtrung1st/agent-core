"""Negative fences for exact tenant generations and disposable verification attempts.

This is provider lifecycle evidence, not IAM or a second Core operation/lease store.
A tombstone cannot authorize creation. The existing provider state volume retains it.
"""
import re
from contextlib import contextmanager
import sqlite3
import threading
import weakref
from pathlib import Path

LABELS = ("agent-core-tenant", "agent-core-session", "agent-core-executor", "agent-core-generation")
CONTRACT = "agent-core-filesystem-contract"


class GenerationFenceError(ValueError):
    """Only lifecycle-fence validation; original provider failures keep their semantics."""


def scope(metadata):
    metadata = metadata or {}
    # Trusted-local pre-tenant and disposable verification calls keep their existing path.
    if not any(key in metadata for key in LABELS[:3]):
        return None
    values = tuple(metadata.get(key, "") for key in LABELS)
    if metadata.get(CONTRACT) != "4" or any(not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{32}", value) for value in values):
        raise GenerationFenceError("tenant_generation_scope")
    return values


class ProviderRetirement:
    def __init__(self, database):
        self.database = str(database)
        Path(database).parent.mkdir(parents=True, exist_ok=True)
        self._locks = weakref.WeakValueDictionary()
        self._gate = threading.Lock()

    @contextmanager
    def _db(self):
        connection = sqlite3.connect(self.database, timeout=5)
        try:
            with connection:
                yield connection
        finally:
            connection.close()

    def _lock(self, owner):
        with self._gate:
            key = owner[-1]
            value = self._locks.get(key)
            if value is None:
                value = threading.RLock()
                self._locks[key] = value
            return value

    def provision(self, owner, action, cleanup):
        if owner is None:
            return action()
        self._record(owner)
        with self._lock(owner):
            if self._record(owner):
                raise GenerationFenceError("provider_scope_retired")
            result = action()
            if self._record(owner):
                cleanup(owner)
                raise GenerationFenceError("provider_scope_retired")
            return result

    def retire(self, owner, cleanup, wait_seconds=15):
        self._record(owner, retire=True)
        lock = self._lock(owner)
        if not lock.acquire(timeout=wait_seconds):
            return False
        try:
            return bool(cleanup(owner))
        finally:
            lock.release()


class GenerationRetirement(ProviderRetirement):
    def __init__(self, database):
        super().__init__(database)
        with self._db() as db:
            db.execute("CREATE TABLE IF NOT EXISTS generation_fences (generation TEXT PRIMARY KEY, tenant TEXT NOT NULL, session TEXT NOT NULL, executor TEXT NOT NULL, retired INTEGER NOT NULL CHECK(retired IN (0,1)))")

    def _record(self, owner, retire=False):
        if scope({**dict(zip(LABELS, owner)), CONTRACT: "4"}) != owner:
            raise GenerationFenceError("tenant_generation_scope")
        tenant, session, executor, generation = owner
        with self._db() as db:
            db.execute("INSERT OR IGNORE INTO generation_fences VALUES (?, ?, ?, ?, ?)", (generation, tenant, session, executor, int(retire)))
            saved = db.execute("SELECT tenant, session, executor, retired FROM generation_fences WHERE generation=?", (generation,)).fetchone()
            if saved[:3] != owner[:3]:
                raise GenerationFenceError("tenant_generation_scope_changed")
            if retire:
                db.execute("UPDATE generation_fences SET retired=1 WHERE generation=?", (generation,))
            return bool(saved[3]) or retire


def verification_scope(metadata):
    metadata = metadata or {}
    value = metadata.get("agent-core-image-verification")
    if value is None or isinstance(value, str) and re.fullmatch(r"[0-9a-f]{32}", value):
        return None
    if not isinstance(value, str) or not re.fullmatch(r"[0-9a-f]{32}\.[0-9a-f]{32}", value) or metadata.get(CONTRACT) != "4" or any(key in metadata for key in LABELS):
        raise GenerationFenceError("tenant_verification_scope")
    return tuple(value.split("."))


class VerificationRetirement(ProviderRetirement):
    def __init__(self, database):
        super().__init__(database)
        with self._db() as db:
            db.execute("CREATE TABLE IF NOT EXISTS verification_fences (attempt TEXT PRIMARY KEY, tenant TEXT NOT NULL, retired INTEGER NOT NULL CHECK(retired IN (0,1)))")

    def _record(self, owner, retire=False):
        if verification_scope({"agent-core-image-verification": ".".join(owner), CONTRACT: "4"}) != owner:
            raise GenerationFenceError("tenant_verification_scope")
        tenant, attempt = owner
        with self._db() as db:
            db.execute("INSERT OR IGNORE INTO verification_fences VALUES (?, ?, ?)", (attempt, tenant, int(retire)))
            saved = db.execute("SELECT tenant, retired FROM verification_fences WHERE attempt=?", (attempt,)).fetchone()
            if saved[0] != tenant:
                raise GenerationFenceError("tenant_verification_scope_changed")
            if retire:
                db.execute("UPDATE verification_fences SET retired=1 WHERE attempt=?", (attempt,))
            return bool(saved[1]) or retire


def retire_containers(service, owner, limit=100):
    return _retire_matching(service, owner, [f"{key}={value}" for key, value in zip(LABELS, owner)], scope, limit)


def retire_verification(service, owner, limit=100):
    return _retire_matching(service, owner, ["agent-core-image-verification=" + ".".join(owner)], verification_scope, limit)


def _retire_matching(service, owner, filters, resolve, limit):
    filters = [*filters, CONTRACT + "=4"]
    summaries = service.docker_client.api.containers(all=True, filters={"label": filters}, trunc=False, limit=limit + 1)
    for summary in summaries[:limit]:
        # An untrusted or stale filtered result cannot turn generation-only equality into ownership.
        labels = summary.get("Labels") or {}
        if resolve(labels) != owner or labels.get(CONTRACT) != "4" or not labels.get("opensandbox.io/id"):
            return False
        container = service.docker_client.containers.get(summary["Id"])
        actual = container.attrs.get("Config", {}).get("Labels") or {}
        if resolve(actual) != owner or actual.get("opensandbox.io/id") != labels["opensandbox.io/id"]:
            return False
        service.delete_sandbox(actual["opensandbox.io/id"])
    if len(summaries) > limit:
        return False
    # Query again; a partial/failed deletion never confirms retirement.
    return not service.docker_client.api.containers(all=True, filters={"label": filters}, trunc=False, limit=limit + 1)


def install(app):
    from fastapi import HTTPException
    from pydantic import BaseModel, ConfigDict, Field
    from opensandbox_server.services.docker.docker_service import DockerSandboxService
    from opensandbox_server.api.lifecycle import sandbox_service

    fences = GenerationRetirement("/var/lib/opensandbox/agent-core-retirements.sqlite")
    verifications = VerificationRetirement("/var/lib/opensandbox/agent-core-retirements.sqlite")
    original = DockerSandboxService._provision_sandbox

    def provision(service, sandbox_id, request, *args, **kwargs):
        try:
            verified = verification_scope(request.metadata)
            if verified is not None:
                return verifications.provision(verified, lambda: original(service, sandbox_id, request, *args, **kwargs),
                    lambda owner: retire_verification(service, owner))
            return fences.provision(scope(request.metadata),
                lambda: original(service, sandbox_id, request, *args, **kwargs),
                lambda owner: retire_containers(service, owner))
        except GenerationFenceError:
            raise HTTPException(409, detail={"code": "GenerationRetired", "message": "Tenant generation is retired or its exact scope is unavailable."}) from None

    DockerSandboxService._provision_sandbox = provision

    class Owner(BaseModel):
        model_config = ConfigDict(extra="forbid")
        tenant: str = Field(pattern=r"^[0-9a-f]{32}$")
        session: str = Field(pattern=r"^[0-9a-f]{32}$")
        executor: str = Field(pattern=r"^[0-9a-f]{32}$")
        generation: str = Field(pattern=r"^[0-9a-f]{32}$")

    def retire(expected: Owner):
        owner = (expected.tenant, expected.session, expected.executor, expected.generation)
        # The shipped deployment selects the existing Docker backend, never an alternative execution engine.
        service = sandbox_service
        if not isinstance(service, DockerSandboxService):
            raise HTTPException(409, detail="Exact Docker generation retirement is unavailable.")
        try:
            confirmed = fences.retire(owner, lambda value: retire_containers(service, value))
            return {"contractVersion": 1, **expected.model_dump(), "retired": confirmed}
        except Exception:
            raise HTTPException(409, detail="Exact tenant generation retirement remains unconfirmed.") from None

    class VerificationOwner(BaseModel):
        model_config = ConfigDict(extra="forbid")
        tenant: str = Field(pattern=r"^[0-9a-f]{32}$")
        attempt: str = Field(pattern=r"^[0-9a-f]{32}$")

    def retire_attempt(expected: VerificationOwner):
        if not isinstance(sandbox_service, DockerSandboxService):
            raise HTTPException(409, detail="Exact Docker verification retirement is unavailable.")
        try:
            confirmed = verifications.retire((expected.tenant, expected.attempt), lambda owner: retire_verification(sandbox_service, owner))
            return {"contractVersion": 1, **expected.model_dump(), "retired": confirmed}
        except Exception:
            raise HTTPException(409, detail="Exact tenant verification retirement remains unconfirmed.") from None

    app.post("/agent-core/verifications/retire")(retire_attempt)

    def capabilities():
        if not isinstance(sandbox_service, DockerSandboxService):
            raise HTTPException(409, detail="Exact Docker generation retirement is unavailable.")
        return {"contractVersion": 1, "retirementFencing": True, "verificationFencing": True}

    app.get("/agent-core/generations/capabilities")(capabilities)
    # Inherits the same private authentication middleware as lifecycle and mount attestation.
    app.post("/agent-core/generations/retire")(retire)
