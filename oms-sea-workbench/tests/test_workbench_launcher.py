"""Exercise the double-click entry point with the real Windows cmd parser."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent.parent


@unittest.skipUnless(os.name == 'nt', 'Windows batch launcher')
class LauncherTests(unittest.TestCase):
    def test_double_click_launcher_reaches_server_from_chinese_directory(self):
        with tempfile.TemporaryDirectory(prefix='oms-launch-') as temporary:
            directory = Path(temporary) / '中文 空格目录'
            directory.mkdir()
            launcher = directory / '启动海运工作台.cmd'
            launcher.write_bytes((ROOT / launcher.name).read_bytes())
            (directory / 'node_modules/react').mkdir(parents=True)
            (directory / 'node_modules/react/package.json').write_text('{}')
            (directory / 'requirements.txt').write_text('')
            # Keep installation and the long-running HTTP server outside this parser test.
            (directory / 'PIL.py').write_text('')
            (directory / 'pypdf.py').write_text('')
            package = directory / 'webapp'
            package.mkdir()
            (package / '__init__.py').write_text('')
            (package / 'server.py').write_text(
                "import json\nfrom pathlib import Path\n"
                "Path('started.json').write_text(json.dumps({'started':True,'cwd':str(Path.cwd())}))\n",
                encoding='utf-8',
            )
            commands = directory / 'bin'
            commands.mkdir()
            (commands / 'npm.cmd').write_bytes(b'@echo off\r\nexit /b 0\r\n')
            environment = dict(os.environ)
            environment['PATH'] = os.pathsep.join([str(commands), str(Path(sys.executable).parent), environment.get('PATH', '')])
            environment['PIP_NO_INDEX'] = '1'
            environment['PIP_DISABLE_PIP_VERSION_CHECK'] = '1'
            comspec = os.environ.get('COMSPEC', r'C:\Windows\System32\cmd.exe')

            def run(script):
                return subprocess.run(
                    f'"{comspec}" /d /s /c ""{script}""',
                    cwd=ROOT, env=environment, stdin=subprocess.DEVNULL,
                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=20,
                )

            baseline = directory / 'probe.cmd'
            baseline.write_bytes(b'@echo off\r\ncd /d "%~dp0"\r\npython -m webapp.server\r\n')
            probe = run(baseline)
            marker = directory / 'started.json'
            self.assertEqual(probe.returncode, 0, probe.stdout.decode(errors='replace'))
            self.assertTrue(marker.exists(), 'cmd test harness did not start Python')
            marker.unlink()

            result = run(launcher)
            output = result.stdout.decode('utf-8', errors='replace')
            self.assertTrue(marker.exists(), output)
            self.assertEqual(result.returncode, 0, output)
            self.assertNotIn('not recognized', output)
            self.assertEqual(json.loads(marker.read_text())['cwd'], str(directory))


if __name__ == '__main__':
    unittest.main()
