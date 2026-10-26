"""Exact provider negative fences: real SQLite + deterministic provisioning-thread races."""
import importlib.util
from pathlib import Path
import tempfile
import threading
import unittest
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location("retirement", Path(__file__).resolve().parents[2] / "deploy/sandbox/retirement.py")
retirement = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retirement)
OWNER = ("a" * 32, "b" * 32, "c" * 32, "d" * 32)


def labels(owner=OWNER):
    return {**dict(zip(retirement.LABELS, owner)), retirement.CONTRACT: "5", "opensandbox.io/id": "owned-provider"}


class RetirementTests(unittest.TestCase):
    def test_retirement_before_provider_thread_is_durable_and_cannot_be_replaced_by_other_owner(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "provider.sqlite"
            fence = retirement.GenerationRetirement(path)
            self.assertTrue(fence.retire(OWNER, lambda _: True))
            reopened = retirement.GenerationRetirement(path)
            with self.assertRaisesRegex(ValueError, "retired"):
                reopened.provision(OWNER, lambda: self.fail("late create"), lambda _: True)
            with self.assertRaisesRegex(ValueError, "scope_changed"):
                reopened.retire(("e" * 32, *OWNER[1:]), lambda _: self.fail("foreign cleanup"))
            self.assertTrue(reopened.retire(OWNER, lambda _: True))

    def test_active_provisioning_makes_retirement_pending_then_is_cleaned_without_releasing_a_late_result(self):
        with tempfile.TemporaryDirectory() as root:
            fence = retirement.GenerationRetirement(Path(root) / "provider.sqlite")
            entered = threading.Event(); release = threading.Event(); errors = []; events = []
            def create():
                entered.set()
                if not release.wait(5):
                    raise TimeoutError("test barrier")
                events.append("created")
                return "provider"
            def cleanup(owner):
                self.assertEqual(OWNER, owner); events.append("retired"); return True
            def worker():
                try:
                    fence.provision(OWNER, create, cleanup)
                    events.append("released")
                except Exception as error:
                    errors.append(error)
            thread = threading.Thread(target=worker); thread.start()
            try:
                self.assertTrue(entered.wait(5))
                self.assertFalse(fence.retire(OWNER, cleanup, wait_seconds=0))
                release.set(); thread.join(5)
                self.assertFalse(thread.is_alive())
                self.assertEqual(["created", "retired"], events)
                self.assertEqual(1, len(errors)); self.assertIn("retired", str(errors[0]))
                self.assertTrue(fence.retire(OWNER, cleanup, wait_seconds=0))
            finally:
                release.set(); thread.join(5)

    def test_incomplete_or_legacy_metadata_cannot_acquire_a_tenant_scope(self):
        self.assertIsNone(retirement.scope({"agent-core-generation": OWNER[3]}))
        for key in retirement.LABELS:
            value = labels(); value.pop(key)
            with self.assertRaises(ValueError):
                retirement.scope(value)
        value = labels(); value[retirement.CONTRACT] = "4"
        with self.assertRaises(ValueError): retirement.scope(value)

    def test_cleanup_rechecks_exact_owner_and_never_deletes_a_foreign_filtered_entry(self):
        remote = labels(); deleted = []
        summaries = [{"Id": "container", "Labels": labels()}]
        service = SimpleNamespace(docker_client=SimpleNamespace(api=SimpleNamespace(containers=lambda **_: summaries),
            containers=SimpleNamespace(get=lambda _: SimpleNamespace(attrs={"Config": {"Labels": remote}}))))
        def delete(provider):
            deleted.append(provider); summaries.clear()
        service.delete_sandbox = delete
        remote[retirement.LABELS[0]] = "e" * 32
        self.assertFalse(retirement.retire_containers(service, OWNER)); self.assertEqual([], deleted)
        remote[retirement.LABELS[0]] = OWNER[0]
        self.assertTrue(retirement.retire_containers(service, OWNER)); self.assertEqual(["owned-provider"], deleted)

    def test_large_owned_batch_makes_bounded_progress_without_confirming_partial_cleanup(self):
        current = [{"Id": str(i), "Labels": {**labels(), "opensandbox.io/id": str(i)}} for i in range(102)]
        deleted = []
        def listing(**options):
            return current[:options["limit"]]
        def get(identity):
            return SimpleNamespace(attrs={"Config": {"Labels": next(item["Labels"] for item in current if item["Id"] == identity)}})
        def delete(identity):
            deleted.append(identity); current[:] = [item for item in current if item["Id"] != identity]
        service = SimpleNamespace(docker_client=SimpleNamespace(api=SimpleNamespace(containers=listing), containers=SimpleNamespace(get=get)), delete_sandbox=delete)
        self.assertFalse(retirement.retire_containers(service, OWNER)); self.assertEqual(100, len(deleted))
        self.assertTrue(retirement.retire_containers(service, OWNER)); self.assertEqual(102, len(deleted))

    def test_bounded_or_partial_cleanup_never_confirms_absence(self):
        service = SimpleNamespace(docker_client=SimpleNamespace(api=SimpleNamespace(containers=lambda **_: [{}] * 101)))
        self.assertFalse(retirement.retire_containers(service, OWNER))
        remote = labels()
        service.docker_client.api.containers = lambda **_: [{"Id": "container", "Labels": remote}]
        service.docker_client.containers = SimpleNamespace(get=lambda _: SimpleNamespace(attrs={"Config": {"Labels": remote}}))
        service.delete_sandbox = lambda _: None
        self.assertFalse(retirement.retire_containers(service, OWNER))


class VerificationRetirementTests(unittest.TestCase):
    owner = (OWNER[0], OWNER[-1])

    def labels(self):
        return {"agent-core-image-verification": self.owner[1], retirement.VERIFICATION_TENANT: self.owner[0], retirement.CONTRACT: "5", "opensandbox.io/id": "verification-provider"}

    def test_sequential_probes_then_durable_attempt_seal_with_independent_runtime_scope(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / "provider.sqlite"
            fence = retirement.VerificationRetirement(path)
            self.assertEqual([0, 1, 2], [fence.provision(self.owner, lambda: n, lambda _: True) for n in range(3)])
            self.assertTrue(fence.retire(self.owner, lambda _: True))
            reopened = retirement.VerificationRetirement(path)
            with self.assertRaisesRegex(ValueError, "retired"):
                reopened.provision(self.owner, lambda: self.fail("late verification"), lambda _: True)
            with self.assertRaisesRegex(ValueError, "scope_changed"):
                reopened.retire(("e" * 32, self.owner[-1]), lambda _: self.fail("foreign cleanup"))
            runtime = retirement.GenerationRetirement(path)
            self.assertEqual("runtime", runtime.provision(OWNER, lambda: "runtime", lambda _: True))
            self.assertTrue(runtime.retire(OWNER, lambda _: True))

    def test_lost_create_thread_cannot_release_late_environment_after_attempt_seal(self):
        with tempfile.TemporaryDirectory() as root:
            fence = retirement.VerificationRetirement(Path(root) / "provider.sqlite")
            entered = threading.Event(); release = threading.Event(); events = []; errors = []
            def create():
                entered.set()
                if not release.wait(5): raise TimeoutError("barrier")
                events.append("created")
            def cleanup(owner):
                self.assertEqual(self.owner, owner); events.append("cleaned"); return True
            def worker():
                try:
                    fence.provision(self.owner, create, cleanup)
                    events.append("released")
                except Exception as error: errors.append(error)
            thread = threading.Thread(target=worker); thread.start()
            try:
                self.assertTrue(entered.wait(5))
                self.assertFalse(fence.retire(self.owner, cleanup, wait_seconds=0))
                release.set(); thread.join(5); self.assertFalse(thread.is_alive())
                self.assertEqual(["created", "cleaned"], events)
                self.assertEqual(1, len(errors)); self.assertIn("retired", str(errors[0]))
                self.assertTrue(fence.retire(self.owner, cleanup, wait_seconds=0))
            finally:
                release.set(); thread.join(5)

    def test_scope_rejects_mixed_or_malformed_owners_without_adopting_legacy(self):
        self.assertIsNone(retirement.verification_scope({"agent-core-image-verification": OWNER[-1]}))
        self.assertEqual(self.owner, retirement.verification_scope(self.labels()))
        self.assertTrue(all(len(value) <= 63 for value in self.labels().values()))
        with self.assertRaises(ValueError): retirement.verification_scope({"agent-core-image-verification": ".".join(self.owner), retirement.CONTRACT: "5"})
        for key in retirement.LABELS:
            with self.assertRaises(ValueError): retirement.verification_scope({**self.labels(), key: OWNER[0]})
        for value in ("bad", ".".join(self.owner).upper(), 5):
            with self.assertRaises(ValueError): retirement.verification_scope({**self.labels(), "agent-core-image-verification": value})
        with self.assertRaises(ValueError): retirement.verification_scope({**self.labels(), retirement.CONTRACT: "4"})

    def test_historical_long_label_is_cleanup_only_and_requires_the_exact_old_owner(self):
        old = {"agent-core-image-verification": ".".join(self.owner), retirement.CONTRACT: "5", "opensandbox.io/id": "old-provider"}
        with self.assertRaises(ValueError): retirement.verification_scope(old)
        pending = [{"Id": "old-container", "Labels": old}]; deleted = []
        def listing(**options):
            return pending if options["filters"]["label"][0] == "agent-core-image-verification=" + ".".join(self.owner) else []
        def delete(identity):
            deleted.append(identity); pending.clear()
        service = SimpleNamespace(docker_client=SimpleNamespace(api=SimpleNamespace(containers=listing),
            containers=SimpleNamespace(get=lambda _: SimpleNamespace(attrs={"Config": {"Labels": old}}))), delete_sandbox=delete)
        self.assertTrue(retirement.retire_verification(service, self.owner)); self.assertEqual(["old-provider"], deleted)
        self.assertTrue(retirement.retire_verification(service, self.owner)); self.assertEqual(["old-provider"], deleted)

    def test_exact_cleanup_rechecks_labels_and_bounded_partial_batches(self):
        current = [{"Id": str(i), "Labels": {**self.labels(), "opensandbox.io/id": str(i)}} for i in range(102)]
        foreign = True; deleted = []
        def listing(**options):
            if options["filters"]["label"] == ["agent-core-image-verification=" + ".".join(self.owner), retirement.CONTRACT + "=5"]: return []
            self.assertEqual(["agent-core-image-verification=" + self.owner[1], retirement.VERIFICATION_TENANT + "=" + self.owner[0], retirement.CONTRACT + "=5"], options["filters"]["label"])
            return current[:options["limit"]]
        def get(identity):
            labels = next(item["Labels"] for item in current if item["Id"] == identity)
            return SimpleNamespace(attrs={"Config": {"Labels": {**labels, retirement.VERIFICATION_TENANT: "e" * 32} if foreign else labels}})
        def delete(identity):
            deleted.append(identity); current[:] = [item for item in current if item["Id"] != identity]
        service = SimpleNamespace(docker_client=SimpleNamespace(api=SimpleNamespace(containers=listing), containers=SimpleNamespace(get=get)), delete_sandbox=delete)
        self.assertFalse(retirement.retire_verification(service, self.owner)); self.assertEqual([], deleted)
        foreign = False
        self.assertFalse(retirement.retire_verification(service, self.owner)); self.assertEqual(100, len(deleted))
        self.assertTrue(retirement.retire_verification(service, self.owner)); self.assertEqual(102, len(deleted))


if __name__ == "__main__":
    unittest.main()
