"""Deterministic checks of the trusted adapter; no Docker or provider keys."""
import copy
import importlib.util
from pathlib import Path, PurePosixPath
import unittest

spec = importlib.util.spec_from_file_location("compatibility", Path(__file__).resolve().parents[2] / "deploy/sandbox/compatibility.py")
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)
ROOTS = [PurePosixPath("/canonical")]
OWNER = "a" * 32
SESSION = "b" * 32
WORK = f"/canonical/agent-{OWNER}/sessions/session-{SESSION}/working"
HOME = f"/canonical/agent-{OWNER}/home"
IMAGE = "registry/image@sha256:" + "d" * 64
IMAGE_ID = "sha256:" + "e" * 64
LABELS = {compat.MARKER: "4", "agent-core-generation": "c" * 32}


def fixture(home="rw", desktop=True):
    binds = [f"{WORK}:/working:rw"] + ([] if home is None else [f"{HOME}:/home:{home}"])
    host = {"Binds": binds, "CapDrop": ["ALL"], "SecurityOpt": ["no-new-privileges=true"], "Privileged": False, "CapAdd": []}
    kwargs = {"image": IMAGE, "labels": LABELS, "host_config": host}
    prepared = compat.prepare_create(kwargs, 501, 20, ROOTS, lambda _: {"Id": IMAGE_ID, "Os": "linux", "Config": {"User": "1000:1000"}})
    attrs = {"Config": {"Labels": prepared["labels"], "User": prepared["user"], "Image": IMAGE}, "Image": IMAGE_ID, "HostConfig": host,
             "Mounts": [{"Source": b.split(":")[0], "Destination": b.split(":")[1], "RW": b.split(":")[2] == "rw", "Type": "bind"} for b in binds]}
    expected = {"ownerLabel": "agent-core-generation", "ownerValue": "c" * 32, "uid": 501, "gid": 20, "image": IMAGE,
                "working": WORK, "home": HOME if home else None, "homeReadOnly": home == "ro"}
    info = {"Name": "docker-desktop" if desktop else "linux-host", "OperatingSystem": "Docker Desktop" if desktop else "Ubuntu 24.04", "OSType": "linux"}
    return kwargs, prepared, attrs, expected, info


class CompatibilityTests(unittest.TestCase):
    def test_preparation_keeps_source_image_and_records_actual_default_identity(self):
        kwargs, prepared, *_ = fixture()
        self.assertEqual(kwargs["image"], prepared["image"])
        self.assertEqual(prepared["user"], "501:20")
        self.assertEqual(prepared["labels"]["agent-core-source-user"], "1000:1000")
        self.assertNotIn("agent-core-source-user", LABELS)
        self.assertEqual(prepared, compat.prepare_create(prepared, 501, 20, ROOTS, lambda _: {"Id": IMAGE_ID, "Os": "linux", "Config": {"User": "1000:1000"}}))

    def test_unowned_container_creation_is_unchanged(self):
        kwargs = {"labels": {"unrelated": "true"}}
        self.assertIs(kwargs, compat.prepare_create(kwargs, 0, 0, [], lambda _: self.fail("must not inspect")))

    def test_native_and_desktop_each_prove_exact_mounts_and_policy(self):
        for desktop in (True, False):
            for home in ("rw", "ro", None):
                with self.subTest(desktop=desktop, home=home):
                    _, _, attrs, expected, info = fixture(home, desktop)
                    result = compat.attest(attrs, expected, info, 501, 20, ROOTS)
                    self.assertEqual(result["topology"], "docker-desktop" if desktop else "linux")
                    self.assertEqual(result["contractVersion"], 4)
                    self.assertNotIn(WORK, str(result))

    def test_wrong_owner_session_image_process_and_daemon_mount_are_refused(self):
        _, _, attrs, expected, info = fixture()
        mutations = [lambda a: a["Config"].update(User="0:0"),
                     lambda a: a["Config"]["Labels"].update({"agent-core-generation": "f" * 32}),
                     lambda a: a["Config"].update(Image="other"),
                     lambda a: a.update(Image="other"),
                     lambda a: a["Mounts"][0].update(Source=WORK.replace(SESSION, "f" * 32)),
                     lambda a: a["Mounts"][1].update(RW=False),
                     lambda a: a["HostConfig"].update(Privileged=True),
                     lambda a: a["HostConfig"].update(CapAdd=["CHOWN"]),
                     lambda a: a["HostConfig"].update(CapDrop=[]),
                     lambda a: a["HostConfig"].update(SecurityOpt=[])]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                modified = copy.deepcopy(attrs); mutate(modified)
                with self.assertRaises(ValueError): compat.attest(modified, expected, info, 501, 20, ROOTS)
        with self.assertRaises(ValueError): compat.attest(attrs, expected, info, 1654, 1654, ROOTS)

    def test_arbitrary_paths_siblings_traversal_extra_mounts_and_root_execution_are_refused(self):
        for source in ("/", "/canonical", WORK + "/..", WORK + "/../working", "/foreign/agent-" + OWNER + "/home", "/canonical/agent-" + OWNER + "/sessions"):
            with self.subTest(source=source), self.assertRaises(ValueError): compat.validate_source(source, ROOTS)
        for binds in ([f"{WORK}:/working:rw", "/var/run/docker.sock:/var/run/docker.sock:rw"],
                      [f"{WORK}:/working:rw", f"{HOME.replace(OWNER, 'f' * 32)}:/home:rw"],
                      [f"{WORK}:/working:ro"], [f"{WORK}:/working:rw", f"{WORK}:/working:rw"]):
            with self.subTest(binds=binds), self.assertRaises(ValueError): compat.validate_binds(binds, ROOTS)
        kwargs, *_ = fixture()
        with self.assertRaises(ValueError): compat.prepare_create(kwargs, 0, 20, ROOTS, lambda _: {})

    def test_unproven_desktop_is_never_treated_as_native_linux(self):
        _, _, attrs, expected, info = fixture()
        for changed in ({"Name": "foreign"}, {"OperatingSystem": "unknown desktop"}, {"OSType": "windows"}):
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                compat.attest(attrs, expected, {**info, **changed}, 501, 20, ROOTS)

if __name__ == "__main__": unittest.main()
