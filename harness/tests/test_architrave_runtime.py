import unittest

from pathlib import Path
import sys


HARNESS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HARNESS))

from architrave_runtime import build_parser, resumable_checkpoint, retry_reconciliation_artifact


class RuntimeParserTests(unittest.TestCase):
    def test_task_add_does_not_replace_selected_action(self) -> None:
        args = build_parser().parse_args([
            "task-add", "run-1",
            "--id", "task-1",
            "--title", "Task",
            "--objective", "Exercise parser",
            "--criteria", "AC-1",
        ])

        self.assertEqual("task-add", args.action)
        self.assertIsNone(args.execution_command)

    def test_task_add_keeps_execution_argv_separate(self) -> None:
        args = build_parser().parse_args([
            "task-add", "run-1",
            "--id", "task-1",
            "--title", "Task",
            "--objective", "Exercise parser",
            "--criteria", "AC-1",
            "--command", "git", "status", "--short",
        ])

        self.assertEqual("task-add", args.action)
        self.assertEqual(["git", "status", "--short"], args.execution_command)

    def test_resolved_external_checkpoint_allows_one_same_attempt_resume(self) -> None:
        checkpoint = {"id": "approval", "resumeTask": "task-1", "status": "RESOLVED"}
        state = {"externalCheckpoints": [checkpoint]}

        self.assertIs(checkpoint, resumable_checkpoint(state, "task-1"))

        checkpoint["resumeConsumedAt"] = "2026-08-24T22:30:00Z"
        self.assertIsNone(resumable_checkpoint(state, "task-1"))

    def test_pending_or_foreign_checkpoint_does_not_bypass_retry_policy(self) -> None:
        state = {
            "externalCheckpoints": [
                {"id": "pending", "resumeTask": "task-1", "status": "PENDING"},
                {"id": "foreign", "resumeTask": "task-2", "status": "RESOLVED"},
            ]
        }

        self.assertIsNone(resumable_checkpoint(state, "task-1"))

    def test_retry_authorization_requires_consumed_not_applied_reconciliation(self) -> None:
        artifact = {
            "id": "reconciled",
            "producer": "reconciliation",
            "consumedByTask": "task-1",
        }
        task = {
            "id": "task-1",
            "status": "READY",
            "attempts": 1,
            "retryPolicy": {"maxAttempts": 1},
            "sideEffect": {
                "state": "NONE",
                "reconciliation": "artifact:reconciled",
            },
        }
        state = {"tasks": [task], "artifacts": [artifact]}

        self.assertIs(artifact, retry_reconciliation_artifact(state, "task-1"))

        artifact["consumedByTask"] = None
        self.assertIsNone(retry_reconciliation_artifact(state, "task-1"))

    def test_task_retry_authorize_parser(self) -> None:
        args = build_parser().parse_args([
            "task-retry-authorize", "run-1", "task-1",
            "--reason", "corrected command quoting",
        ])

        self.assertEqual("task-retry-authorize", args.action)
        self.assertEqual("corrected command quoting", args.reason)


if __name__ == "__main__":
    unittest.main()