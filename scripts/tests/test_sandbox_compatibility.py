"""Deterministic checks of the trusted adapter; no Docker or provider keys."""
import copy
import importlib.util
from pathlib import Path, PurePosixPath
import unittest
import sys

spec = importlib.util.spec_from_file_location("compatibility", Path(__file__).resolve().parents[2] / "deploy/sandbox/compatibility.py")
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)
ROOTS = [PurePosixPath("/canonical")]
OWNER = "a" * 32
SESSION = "b" * 32
WORK = f"/canonical/agent-{OWNER}/sessions/session-{SESSION}/working"
TENANT = "1" * 32
SPACE = "2" * 32
PRIVATE = f"/canonical/tenant-{TENANT}/spaces/space-{'3' * 32}/home"
HOME = f"/canonical/tenant-{TENANT}/spaces/space-{SPACE}/home"
IMAGE = "registry/image@sha256:" + "d" * 64
IMAGE_ID = "sha256:" + "e" * 64
LABELS = {compat.MARKER: "5", "agent-core-generation": "c" * 32, "agent-core-tenant": TENANT}


def fixture(home="rw", desktop=True, working="rw", private=None):
    binds = [f"{WORK}:/working:{working}"] + ([] if home is None else [f"{HOME}:/home:{home}"]) + ([] if private is None else [f"{PRIVATE}:/private:{private}"])
    host = {"Binds": binds, "CapDrop": ["ALL"], "SecurityOpt": ["no-new-privileges=true"], "Privileged": False, "CapAdd": []}
    kwargs = {"image": IMAGE, "labels": LABELS, "host_config": host}
    prepared = compat.prepare_create(kwargs, 501, 20, ROOTS, lambda _: {"Id": IMAGE_ID, "Os": "linux", "Config": {"User": "1000:1000"}})
    attrs = {"Config": {"Labels": prepared["labels"], "User": prepared["user"], "Image": IMAGE}, "Image": IMAGE_ID, "HostConfig": host,
             "Mounts": [{"Source": b.split(":")[0], "Destination": b.split(":")[1], "RW": b.split(":")[2] == "rw", "Type": "bind"} for b in binds]}
    expected = {"ownerLabel": "agent-core-generation", "ownerValue": "c" * 32, "uid": 501, "gid": 20, "image": IMAGE,
                "working": WORK, "home": HOME if home else None, "homeReadOnly": home == "ro", "workingReadOnly": working == "ro", "privateHome": PRIVATE if private else None, "privateReadOnly": private == "ro"}
    info = {"Name": "docker-desktop" if desktop else "linux-host", "OperatingSystem": "Docker Desktop" if desktop else "Ubuntu 24.04", "OSType": "linux"}
    return kwargs, prepared, attrs, expected, info


class CompatibilityTests(unittest.TestCase):
    def test_evaluation_mounts_require_exact_disposable_identity_read_only_input_and_no_private_mount(self):
        execution = "4" * 32
        labels = {**LABELS, "agent-core-generation": execution, "agent-core-session": execution, "agent-core-evaluation-attempt": "5" * 32}
        working = f"/canonical/.evaluations/{execution}/working:/working:rw"
        home = f"/canonical/.evaluations/{execution}/home:/home:ro"
        self.assertEqual({"/home", "/working"}, set(compat.validate_binds([working, home], ROOTS, TENANT, labels)))
        for binds, proof in (([working, home.replace(":ro", ":rw")], labels), ([working], labels),
                             ([working, home, f"{PRIVATE}:/private:ro"], labels), ([working, home], LABELS),
                             ([working, home.replace(execution, "6" * 32)], labels), ([working, home], {**labels, "agent-core-session": "7" * 32})):
            with self.subTest(binds=binds), self.assertRaises(ValueError):
                compat.validate_binds(binds, ROOTS, TENANT, proof)

    def test_browser_acceptance_uses_a_disposable_namespace_accepted_by_mount_authority(self):
        sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
        runner_spec = importlib.util.spec_from_file_location("sandbox_runner", Path(__file__).resolve().parents[1] / "opensandbox-local.py")
        runner = importlib.util.module_from_spec(runner_spec)
        runner_spec.loader.exec_module(runner)
        first = runner.ui_workspace_root("/canonical")
        second = runner.ui_workspace_root("/canonical")
        self.assertNotEqual(first, second)
        for root in (first, second):
            working = f"{root}/agent-{OWNER}/sessions/session-{SESSION}/working"
            home = f"{root}/tenant-{TENANT}/spaces/space-{SPACE}/home"
            for mode in ("rw", "ro", None):
                binds = [f"{working}:/working:rw"] + ([] if mode is None else [f"{home}:/home:{mode}"])
                self.assertIn("/working", compat.validate_binds(binds, ROOTS, TENANT))
            self.assertTrue(compat.validate_source(f"{root}/.verification/{SESSION}/working", ROOTS))
        with self.assertRaises(ValueError):
            compat.validate_source(f"/canonical/ui-deadbeef/.verification/{SESSION}/working", ROOTS)

    def test_preparation_keeps_source_image_and_records_actual_default_identity(self):
        kwargs, prepared, *_ = fixture()
        self.assertEqual(kwargs["image"], prepared["image"])
        self.assertEqual(prepared["user"], "501:20")
        self.assertEqual(prepared["labels"]["agent-core-source-user"], "1000:1000")
        self.assertNotIn("agent-core-source-user", LABELS)
        self.assertEqual(prepared, compat.prepare_create(prepared, 501, 20, ROOTS, lambda _: {"Id": IMAGE_ID, "Os": "linux", "Config": {"User": "1000:1000"}}))

    def test_tenant_verification_owner_is_prepared_and_attested_without_accepting_a_composite_runtime_generation(self):
        kwargs, _, attrs, expected, info = fixture()
        owner = "b" * 32
        owned = {compat.MARKER: "5", "agent-core-image-verification": owner, "agent-core-verification-tenant": TENANT, "agent-core-tenant": TENANT}
        self.assertTrue(all(len(value) <= 63 for value in owned.values()))
        prepared = compat.prepare_create({**kwargs, "labels": owned}, 501, 20, ROOTS,
            lambda _: {"Id": IMAGE_ID, "Os": "linux", "Config": {"User": "1000:1000"}})
        self.assertEqual("501:20", prepared["user"])
        attrs["Config"]["Labels"] = prepared["labels"]
        expected.update(ownerLabel="agent-core-image-verification", ownerValue=owner)
        self.assertEqual(5, compat.attest(attrs, expected, info, 501, 20, ROOTS)["contractVersion"])
        self.assertFalse(compat.managed({compat.MARKER: "5", "agent-core-generation": "a" * 32 + "." + owner}))

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
                    self.assertEqual(result["contractVersion"], 5)
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
        for source in ("/", "/canonical", WORK + "/..", WORK + "/../working", f"/canonical/agent-{OWNER}/home", "/foreign/agent-" + OWNER + "/home", "/canonical/agent-" + OWNER + "/sessions"):
            with self.subTest(source=source), self.assertRaises(ValueError): compat.validate_source(source, ROOTS)
        for binds in ([f"{WORK}:/working:rw", "/var/run/docker.sock:/var/run/docker.sock:rw"],
                      [f"{WORK}:/working:rw", f"{HOME.replace(TENANT, 'f' * 32)}:/home:rw"],
                      [f"{WORK}:/working:rw", f"{WORK}:/working:rw"]):
            with self.subTest(binds=binds), self.assertRaises(ValueError): compat.validate_binds(binds, ROOTS)
        kwargs, *_ = fixture()
        with self.assertRaises(ValueError): compat.prepare_create(kwargs, 0, 20, ROOTS, lambda _: {})

    def test_space_home_private_aliases_and_working_read_only_are_attested_exactly(self):
        for working in ("ro", "rw"):
            for home in ("ro", "rw", None):
                for private in ("ro", "rw", None):
                    with self.subTest(working=working, home=home, private=private):
                        _, _, attrs, expected, info = fixture(home, working=working, private=private)
                        self.assertEqual(5, compat.attest(attrs, expected, info, 501, 20, ROOTS)["contractVersion"])
                        wrong = {**expected, "workingReadOnly": not expected["workingReadOnly"]}
                        with self.assertRaises(ValueError): compat.attest(attrs, wrong, info, 501, 20, ROOTS)
                        if private:
                            with self.assertRaises(ValueError): compat.attest(attrs, {**expected, "privateReadOnly": not expected["privateReadOnly"]}, info, 501, 20, ROOTS)
        with self.assertRaises(ValueError):
            compat.validate_binds([f"{WORK}:/working:rw", f"{HOME}:/home:ro", f"{HOME}:/private:rw"], ROOTS, TENANT)
        with self.assertRaises(ValueError):
            compat.validate_binds([f"{WORK}:/working:rw", f"{PRIVATE}:/private:rw"], ROOTS, "f" * 32)
        with self.assertRaises(ValueError):
            compat.validate_binds([f"{HOME}:/working:rw"], ROOTS, TENANT)
        self.assertEqual({"/working", "/home", "/private"}, set(compat.validate_binds([
            f"/canonical/.verification/{SESSION}/working:/working:ro",
            f"/canonical/.verification/{SESSION}/home:/home:ro",
            f"/canonical/.verification/{SESSION}/private:/private:rw"], ROOTS)))

    def test_unproven_desktop_is_never_treated_as_native_linux(self):
        _, _, attrs, expected, info = fixture()
        for changed in ({"Name": "foreign"}, {"OperatingSystem": "unknown desktop"}, {"OSType": "windows"}):
            with self.subTest(changed=changed), self.assertRaises(ValueError):
                compat.attest(attrs, expected, {**info, **changed}, 501, 20, ROOTS)

if __name__ == "__main__": unittest.main()
