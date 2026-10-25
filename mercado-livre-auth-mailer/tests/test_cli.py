import unittest

from shop_auth_codes.cli import build_parser


class CliTests(unittest.TestCase):
    def test_exposes_setup_dry_run_and_run_commands(self):
        parser = build_parser()

        self.assertEqual(parser.parse_args(["setup"]).command, "setup")
        self.assertEqual(parser.parse_args(["dry-run"]).command, "dry-run")
        self.assertEqual(parser.parse_args(["run"]).command, "run")


if __name__ == "__main__":
    unittest.main()
