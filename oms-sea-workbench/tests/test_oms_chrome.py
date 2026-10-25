import tempfile
import unittest
from pathlib import Path

import oms_chrome


class OmsChromeTests(unittest.TestCase):
    def test_launch_args_use_a_dedicated_profile_and_cdp_port(self):
        self.assertEqual(
            oms_chrome.build_launch_args(Path("C:/Users/test/AppData/Local/OMS/Chrome")),
            [
                "--remote-debugging-port=9223",
                "--user-data-dir=C:\\Users\\test\\AppData\\Local\\OMS\\Chrome",
                "https://app.upseller.com/",
            ],
        )

    def test_profile_is_persisted_outside_the_project(self):
        profile = oms_chrome.profile_directory({"LOCALAPPDATA": "C:/Users/test/AppData/Local"})
        self.assertEqual(profile, Path("C:/Users/test/AppData/Local/OMS海运录入/chrome-profile"))

    def test_explicit_chrome_path_is_selected(self):
        with tempfile.TemporaryDirectory() as directory:
            chrome = Path(directory) / "chrome.exe"
            chrome.touch()
            self.assertEqual(oms_chrome.find_chrome({"OMS_CHROME_PATH": str(chrome)}), chrome)

    def test_launch_creates_the_profile_and_starts_chrome_with_expected_args(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            chrome = root / "chrome.exe"
            chrome.touch()
            profile = root / "local" / "OMS海运录入" / "chrome-profile"
            calls = []

            result = oms_chrome.launch_chrome(
                {"OMS_CHROME_PATH": str(chrome), "LOCALAPPDATA": str(root / "local")},
                popen=lambda args, **kwargs: calls.append((args, kwargs)),
            )

            self.assertEqual(result, profile)
            self.assertTrue(profile.is_dir())
            self.assertEqual(
                calls,
                [
                    (
                        [str(chrome), *oms_chrome.build_launch_args(profile)],
                        {"close_fds": True},
                    )
                ],
            )


if __name__ == "__main__":
    unittest.main()
