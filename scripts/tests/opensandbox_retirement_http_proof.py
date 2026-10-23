"""Opt-in pinned-image ASGI proof. No Docker socket, host service, port or provider access.

Run inside the existing pinned server image with network disabled. Its real FastAPI,
AuthMiddleware and DockerSandboxService hook execute against a controlled Docker peer.
"""
import asyncio
import importlib.util
import sys
import types
from types import SimpleNamespace

import httpx
from fastapi import FastAPI, HTTPException
from opensandbox_server.middleware.auth import AuthMiddleware
from opensandbox_server.services.docker.docker_service import DockerSandboxService

spec = importlib.util.spec_from_file_location("retirement", "/proof/retirement.py")
retirement = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retirement)
owner = ("1" * 32, "2" * 32, "3" * 32, "4" * 32)
metadata = {**dict(zip(retirement.LABELS, owner)), retirement.CONTRACT: "4", "opensandbox.io/id": "controlled-provider"}
current = []; deleted = []; creates = []
provider = DockerSandboxService.__new__(DockerSandboxService)
provider.docker_client = SimpleNamespace(api=SimpleNamespace(containers=lambda **options: [item for item in current if all(item["Labels"].get(key) == value for key, value in (label.split("=", 1) for label in options["filters"]["label"]))]),
    containers=SimpleNamespace(get=lambda identity: SimpleNamespace(attrs={"Config": {"Labels": next(item["Labels"] for item in current if item["Id"] == identity)}})))
def delete(value):
    deleted.append(value); current[:] = [item for item in current if item["Labels"]["opensandbox.io/id"] != value]
provider.delete_sandbox = delete

def create(service, sandbox_id, request, *args, **kwargs):
    if sandbox_id == "invalid-provider":
        raise ValueError("controlled-provider-error")
    creates.append(sandbox_id); current.append({"Id": sandbox_id, "Labels": {**request.metadata, "opensandbox.io/id": sandbox_id}})
    return SimpleNamespace(id=sandbox_id)
DockerSandboxService._provision_sandbox = create
lifecycle = types.ModuleType("opensandbox_server.api.lifecycle")
lifecycle.sandbox_service = provider
sys.modules[lifecycle.__name__] = lifecycle
app = FastAPI()
app.add_middleware(AuthMiddleware, config=SimpleNamespace(server=SimpleNamespace(api_key="synthetic-retirement-proof-key")))
retirement.install(app)

async def proof():
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://controlled") as client:
        payload = dict(zip(("tenant", "session", "executor", "generation"), owner))
        path = "/agent-core/generations/retire"
        assert (await client.post(path, json=payload)).status_code == 401
        assert (await client.get("/agent-core/generations/capabilities")).status_code == 401
        headers = {"OPEN-SANDBOX-API-KEY": "synthetic-retirement-proof-key"}
        assert (await client.post(path, json={**payload, "extra": True}, headers=headers)).status_code == 422
        capabilities = await client.get("/agent-core/generations/capabilities", headers=headers)
        assert capabilities.status_code == 200 and capabilities.json() == {"contractVersion": 1, "retirementFencing": True, "verificationFencing": True, "verificationScopeVersion": 2}
        try:
            provider._provision_sandbox("invalid-provider", SimpleNamespace(metadata={"agent-core-generation": "5" * 32}))
            raise AssertionError("original provider error was swallowed")
        except ValueError as error:
            assert str(error) == "controlled-provider-error"
        provider._provision_sandbox("controlled-provider", SimpleNamespace(metadata=metadata))
        result = await client.post(path, json=payload, headers=headers)
        assert result.status_code == 200, result.text
        assert result.json() == {"contractVersion": 1, **payload, "retired": True}
        assert deleted == ["controlled-provider"]
        try:
            provider._provision_sandbox("late-provider", SimpleNamespace(metadata=metadata))
            raise AssertionError("retired provisioning was released")
        except HTTPException as error:
            assert error.status_code == 409
        assert creates == ["controlled-provider"]
        foreign = await client.post(path, json={**payload, "tenant": "9" * 32}, headers=headers)
        assert foreign.status_code == 409
        assert deleted == ["controlled-provider"]
        verification = {"tenant": owner[0], "attempt": owner[3]}
        verify_path = "/agent-core/verifications/retire"
        assert (await client.post(verify_path, json=verification)).status_code == 401
        assert (await client.post(verify_path, json={**verification, "session": owner[1]}, headers=headers)).status_code == 422
        verify_labels = {"agent-core-image-verification": owner[3], retirement.VERIFICATION_TENANT: owner[0], retirement.CONTRACT: "4"}
        for index in range(3):
            provider._provision_sandbox("verify-" + str(index), SimpleNamespace(metadata=verify_labels))
        result = await client.post(verify_path, json=verification, headers=headers)
        assert result.status_code == 200 and result.json() == {"contractVersion": 1, **verification, "retired": True}
        assert current == [] and deleted == ["controlled-provider", "verify-0", "verify-1", "verify-2"]
        try:
            provider._provision_sandbox("late-verify", SimpleNamespace(metadata=verify_labels))
            raise AssertionError("retired verification was released")
        except HTTPException as error:
            assert error.status_code == 409
        assert len(creates) == 4
        assert (await client.post(verify_path, json={**verification, "tenant": "9" * 32}, headers=headers)).status_code == 409
        print("Pinned-image ASGI/auth/typed-retirement/provider-hook proof passed; Docker peer was controlled, no daemon socket mounted.")

asyncio.run(proof())
