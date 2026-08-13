import unittest

from pathlib import Path
import sys


HARNESS = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HARNESS))

from architrave_runtime import build_parser


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


if __name__ == "__main__":
    unittest.main()