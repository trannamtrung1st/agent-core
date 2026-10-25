"""Agent Core's narrow adapter for the pinned trusted Docker server.

No host mutation endpoint. The only preparation is selecting the deployment's
non-root identity at container creation, before execd or user code can run.
"""
import os
import re
from pathlib import PurePosixPath

CONTRACT = "5"
MARKER = "agent-core-filesystem-contract"
OWNER_LABELS = ("agent-core-generation", "agent-core-image-verification")
SCOPE = re.compile(r"(?:[0-9a-f]{32}/)?(?:agent-[0-9a-f]{32}/(?:sessions/session-[0-9a-f]{32}/working)|tenant-[0-9a-f]{32}/spaces/space-[0-9a-f]{32}/home|\.verification/[0-9a-f]{32}/(?:working|home|private))\Z")


def managed(labels):
    return labels.get(MARKER) == CONTRACT and sum(k in labels for k in OWNER_LABELS) == 1 and all(
        re.fullmatch(r"[0-9a-f]{32}", labels[key])
        for key in OWNER_LABELS if key in labels)


def validate_source(source, roots):
    path = PurePosixPath(source)
    if not path.is_absolute() or str(path) != source or ".." in path.parts:
        raise ValueError("mount_path")
    for root in roots:
        try:
            relative = path.relative_to(root).as_posix()
        except ValueError:
            continue
        if SCOPE.fullmatch(relative):
            return relative
    raise ValueError("mount_scope")


def validate_binds(binds, roots, tenant=None):
    mounts = {}
    scopes = {}
    for bind in binds:
        source, target, mode = bind.rsplit(":", 2)
        if target in ("/working", "/home", "/private"):
            if target in mounts or mode not in ("rw", "ro"):
                raise ValueError("mount_policy")
            scopes[target] = validate_source(source, roots)
            scope = re.sub(r"^[0-9a-f]{32}/", "", scopes[target])
            if target == "/working" and not (re.fullmatch(r"agent-[0-9a-f]{32}/sessions/session-[0-9a-f]{32}/working", scope)
                    or re.fullmatch(r"\.verification/[0-9a-f]{32}/working", scope)):
                raise ValueError("working_owner")
            mounts[target] = (source, mode)
        elif target != "/opt/opensandbox" or source.startswith("/"):
            raise ValueError("extra_mount")
    if "/working" not in mounts:
        raise ValueError("working_mount")
    working = scopes["/working"]
    namespace = working.split("/", 1)[0] + "/" if re.match(r"^[0-9a-f]{32}/", working) else ""
    working = working[len(namespace):]
    for target in ("/home", "/private"):
        if target not in scopes:
            continue
        candidate = scopes[target]
        if working.startswith(".verification/"):
            if candidate != namespace + working.rsplit("/", 1)[0] + target:
                raise ValueError("verification_owner")
        else:
            if not tenant or not re.fullmatch(r"[0-9a-f]{32}", tenant):
                raise ValueError("tenant_owner")
            if not candidate.startswith(namespace + "tenant-" + tenant + "/spaces/space-"):
                raise ValueError("space_owner")
    if "/home" in mounts and "/private" in mounts and mounts["/home"][0] == mounts["/private"][0] and mounts["/home"][1] != mounts["/private"][1]:
        raise ValueError("alias_authority")
    return mounts


def prepare_create(kwargs, uid, gid, roots, inspect_image):
    labels = kwargs.get("labels") or {}
    if not managed(labels):
        return kwargs
    config = kwargs.get("host_config") or {}
    if uid <= 0 or gid <= 0 or config.get("Privileged") or config.get("CapAdd") or "ALL" not in config.get("CapDrop", []):
        raise ValueError("execution_security")
    if "no-new-privileges=true" not in config.get("SecurityOpt", []):
        raise ValueError("execution_security")
    validate_binds(config.get("Binds", []), roots, labels.get("agent-core-tenant"))
    image = inspect_image(kwargs["image"])
    if image.get("Os") != "linux":
        raise ValueError("image_platform")
    # Keep the immutable selected image intact. Docker USER is a deployment
    # execution parameter; no image build, tag substitution, chown or root stage.
    return {**kwargs, "user": f"{uid}:{gid}", "labels": {**labels,
        "agent-core-source-image": image["Id"],
        "agent-core-source-user": image.get("Config", {}).get("User", "")}}


def attest(attrs, expected, info, uid, gid, roots):
    labels = attrs.get("Config", {}).get("Labels") or {}
    if not managed(labels) or labels.get(expected["ownerLabel"]) != expected["ownerValue"]:
        raise ValueError("generation_identity")
    if expected["uid"] != uid or expected["gid"] != gid or attrs["Config"].get("User") != f"{uid}:{gid}":
        raise ValueError("execution_identity")
    config = attrs["HostConfig"]
    if config.get("Privileged") or config.get("CapAdd") or "ALL" not in config.get("CapDrop", []) or "no-new-privileges=true" not in config.get("SecurityOpt", []):
        raise ValueError("execution_security")
    selected = expected["image"]
    if attrs["Config"].get("Image") != selected or labels.get("agent-core-source-image") != attrs["Image"]:
        raise ValueError("image_identity")
    binds = validate_binds(config.get("Binds", []), roots, labels.get("agent-core-tenant"))
    wanted = {"/working": (expected["working"], "ro" if expected.get("workingReadOnly", False) else "rw")}
    if expected.get("home") is not None:
        wanted["/home"] = (expected["home"], "ro" if expected["homeReadOnly"] else "rw")
    if expected.get("privateHome") is not None:
        wanted["/private"] = (expected["privateHome"], "ro" if expected["privateReadOnly"] else "rw")
    if binds != wanted:
        raise ValueError("backing_path")
    actual = {m["Destination"]: (m["Source"], "rw" if m["RW"] else "ro")
              for m in attrs["Mounts"] if m["Destination"] in wanted and m["Type"] == "bind"}
    if actual != wanted:
        raise ValueError("daemon_mount")
    desktop = info.get("OperatingSystem") == "Docker Desktop" and info.get("OSType") == "linux" and info.get("Name") == "docker-desktop"
    if not desktop and (info.get("OSType") != "linux" or "desktop" in info.get("OperatingSystem", "").lower()):
        raise ValueError("unsupported_topology")
    return {"contractVersion": 5, "topology": "docker-desktop" if desktop else "linux",
            "imageId": attrs["Image"], "imageUser": labels.get("agent-core-source-user", ""), "runtimeUser": attrs["Config"]["User"]}


def install(app, config):
    import docker
    from docker.api.container import ContainerApiMixin
    from fastapi import HTTPException
    from pydantic import BaseModel, ConfigDict, Field

    uid = int(os.environ["AGENTCORE_SANDBOX_UID"])
    gid = int(os.environ["AGENTCORE_SANDBOX_GID"])
    roots = [PurePosixPath(p) for p in config.storage.allowed_host_paths]
    original = ContainerApiMixin.create_container

    def create(client, *args, **kwargs):
        if args and managed(kwargs.get("labels") or {}):
            raise ValueError("unsupported_provider_contract")
        try:
            prepared = prepare_create(kwargs, uid, gid, roots, client.inspect_image)
        except ValueError:
            raise HTTPException(409, detail={"code": "MountIncompatible", "message": "Automatic preparation could not establish safe filesystem access."}) from None
        return original(client, *args, **prepared)

    ContainerApiMixin.create_container = create

    class Expected(BaseModel):
        model_config = ConfigDict(extra="forbid")
        ownerLabel: str = Field(pattern=r"^agent-core-(generation|image-verification)$")
        ownerValue: str = Field(pattern=r"^(?:[0-9a-f]{32}\.)?[0-9a-f]{32}$")
        uid: int = Field(gt=0)
        gid: int = Field(gt=0)
        image: str = Field(max_length=512)
        working: str = Field(max_length=4096)
        home: str | None = Field(default=None, max_length=4096)
        homeReadOnly: bool
        workingReadOnly: bool
        privateHome: str | None = Field(default=None, max_length=4096)
        privateReadOnly: bool

    # Inherits the existing server authentication middleware. Workloads have no
    # server key or Docker socket. Never return daemon errors or host paths.
    def check(sandbox_id: str, expected: Expected):
        client = docker.from_env()
        try:
            containers = client.containers.list(all=True, filters={"label": f"opensandbox.io/id={sandbox_id}"})
            if len(containers) != 1:
                raise ValueError("sandbox_identity")
            return attest(containers[0].attrs, expected.model_dump(), client.info(), uid, gid, roots)
        except Exception:
            raise HTTPException(409, detail="Sandbox filesystem compatibility could not be established.") from None
        finally:
            client.close()

    app.post("/agent-core/mounts/{sandbox_id}")(check)
