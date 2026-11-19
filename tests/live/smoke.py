"""Real stdio MCP checks, restricted to the fixture's dedicated VS instance."""
import argparse
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import threading
import time


# Debugging options, as Page.Name, that the extension turns off when a client connects.
PROMPTING_OPTIONS = ['EditAndContinue.InvokedByCommands', 'EditAndContinue.WarnAboutStaleCode',
                     'General.WarnIfNoSymbols', 'General.WarnIfNoUserCode']


def close_window(pid, title):
    """Closes the visible window of process pid with this title, as a person would."""
    user32 = ctypes.windll.user32
    found = []

    def visit(window, _):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(window, ctypes.byref(owner))
        text = ctypes.create_unicode_buffer(512)
        user32.GetWindowTextW(window, text, 512)
        if owner.value == pid and user32.IsWindowVisible(window) and text.value == title:
            found.append(window)
        return True

    user32.EnumWindows(ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)(visit), 0)
    assert found, 'No window titled ' + title
    user32.PostMessageW(found[0], 0x0010, 0, 0)  # WM_CLOSE


def main():
    parser = argparse.ArgumentParser()
    for name in ('shim', 'data', 'fixture', 'pid', 'output'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--profiles', action='store_true')
    args = parser.parse_args()
    fixture = Path(args.fixture).resolve()
    record = json.loads((Path(args.data) / ('inst-' + args.pid + '.json')).read_text(encoding='utf-8-sig'))
    record = {k.lower(): v for k, v in record.items()}
    workspace = {k.lower(): v for k, v in record['workspace'].items()}
    assert record['pid'] == int(args.pid)
    assert Path(workspace['file']).resolve() == fixture / 'DebugTarget.sln'
    instance = args.pid
    env = dict(os.environ, VSDBGMCP_DATA_DIR=args.data)
    child = None
    replies = queue.Queue()
    results = []
    sequence = 0
    capture_id = None
    collector_stopped = False

    def receive(process):
        for line in process.stdout:
            try:
                replies.put(json.loads(line))
            except ValueError:
                pass
    def keep_stderr(process):
        # Kept beside the results: a tool that fails says why only on stderr.
        with open(Path(args.output).with_name('shim-stderr.log'), 'a', encoding='utf-8') as log:
            for line in process.stderr:
                log.write(line)
                log.flush()

    def connect():
        nonlocal child
        child = subprocess.Popen([args.shim, '--cwd', str(fixture)], stdin=subprocess.PIPE,
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)
        threading.Thread(target=receive, args=(child,), daemon=True).start()
        threading.Thread(target=keep_stderr, args=(child,), daemon=True).start()
        request('initialize', dict(protocolVersion='2025-06-18', capabilities={}, clientInfo=dict(name='vsdbgmcp-live-tests', version='1')))
        child.stdin.write(json.dumps(dict(jsonrpc='2.0', method='notifications/initialized')) + '\n')
        child.stdin.flush()

    def request(method, params, timeout=75):
        nonlocal sequence
        sequence += 1
        child.stdin.write(json.dumps(dict(jsonrpc='2.0', id=sequence, method=method, params=params)) + '\n')
        child.stdin.flush()
        deadline = time.monotonic() + timeout
        while True:
            reply = replies.get(timeout=max(.01, deadline-time.monotonic()))
            if reply.get('id') != sequence:
                continue
            if 'error' in reply:
                raise RuntimeError(reply['error'])
            return reply['result']

    def call(name, _instance=True, **arguments):
        if _instance:
            arguments['instance'] = instance
        response = request('tools/call', dict(name=name, arguments=arguments))
        # Persist tool replies, never the instance registration token.
        results.append(dict(tool=name, arguments=arguments, response=response))
        Path(args.output).write_text(json.dumps(results, indent=2), encoding='utf-8')
        if response.get('isError'):
            raise RuntimeError(response)
        return response

    def text(response):
        return '\n'.join(c.get('text', '') for c in response.get('content', []) if c.get('type') == 'text')

    def operation_id(response):
        found = re.search(r'(?:\bBuild |\[|\bOperation: )([a-z0-9]{6})(?:\]|[:, ])', text(response))
        assert found, response
        return found.group(1)

    def data(response):
        value = response.get('structuredContent')
        if value is None:
            value = json.loads(text(response))
        return value

    def field(value, key):
        return next((v for k, v in value.items() if k.lower() == key.lower()), None)

    def dte_command(*arguments):
        return [os.path.expandvars(r'%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile',
                '-ExecutionPolicy', 'Bypass', '-File', str(Path(__file__).with_name('dte.ps1')), '-VsPid', args.pid,
                *arguments]

    def dte(*arguments):
        return subprocess.run(dte_command(*arguments), capture_output=True, text=True, check=True, timeout=60).stdout

    def complete(operation):
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            value = data(call('operation_status', operationId=operation, waitSeconds=30))
            if field(value, 'state') != 'pending':
                return value
        raise TimeoutError('Operation did not complete: ' + operation)

    try:
        connect()
        catalog = request('tools/list', {})['tools']
        assert len(catalog) == 60
        assert {'operation_status', 'build_diagnostics'} <= {t['name'] for t in catalog}
        assert 'operation_reconcile' not in {t['name'] for t in catalog}
        for name in ('operation_status', 'build_diagnostics'):
            assert next(t for t in catalog if t['name'] == name).get('outputSchema')
        status_schema = next(t for t in catalog if t['name'] == 'operation_status')['outputSchema']
        assert len(json.dumps(status_schema)) < 10000, 'Status schema includes too much internal state'
        assert 'terminal' not in json.dumps(status_schema)
        Path(args.output).with_name('catalog.json').write_text(json.dumps(catalog, indent=2), encoding='utf-8')
        assert field(data(call('debug_state')), 'mode') == 'design'
        call('config')
        call('build_output')
        for mode in ('rebuild', 'build'):
            reply = call('build', mode=mode, configuration='Debug', platform='x64', waitSeconds=0, requestId='live-' + mode)
            operation = operation_id(reply)
            if mode == 'rebuild':
                child.terminate()
                child.wait(timeout=5)
                connect()
            result = complete(operation)
            assert field(result, 'state') == 'succeeded', result
            call('build_log', operationId=operation)
            again = call('build', mode=mode, configuration='Debug', platform='x64', waitSeconds=0, requestId='live-' + mode)
            assert operation in text(again)

        source = fixture / 'DebugTarget' / 'main.cpp'
        original = source.read_text()
        try:
            source.write_text(original + '\n#error LIVE_EXPECTED_BUILD_FAILURE\n')
            reply = call('build', requestId='live-failed', waitSeconds=0)
            failed = complete(operation_id(reply))
            assert field(failed, 'state') == 'failed', failed
            assert 'LIVE_EXPECTED_BUILD_FAILURE' in text(call('build_log', operationId=field(failed, 'operationId')))
        finally:
            source.write_text(original)

        # A controlled slow target gives cancellation time to reach the build manager.
        target = fixture / 'Directory.Build.targets'
        target.write_text('<Project><Target Name="LiveDelay" BeforeTargets="PrepareForBuild">'
                          '<Exec Command="powershell -NoProfile -Command &quot;Start-Sleep -Seconds 20&quot;" />'
                          '</Target></Project>')
        try:
            reply = call('build', mode='rebuild', requestId='live-cancel', waitSeconds=0)
            cancel_id = operation_id(reply)
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                progress = data(call('operation_status', operationId=cancel_id, waitSeconds=1))
                if field(progress, 'state') == 'pending':
                    break
                assert field(progress, 'state') == 'pending', progress
            call('build_cancel', operationId=cancel_id)
            cancelled = complete(cancel_id)
            assert field(cancelled, 'state') == 'cancelled', cancelled

            reply = call('build', mode='rebuild', requestId='live-follow-action', waitSeconds=0)
            follow_id = operation_id(reply)
            pending = data(call('operation_status', operationId=follow_id))
            assert field(pending, 'state') == 'pending', pending
            assert field(pending, 'details') is None
            assert field(pending, 'terminal') is None
            action = field(pending, 'nextAction')
            assert field(action, 'tool') == 'operation_status', action
            assert field(action, 'arguments')['operationId'] == follow_id
            # Follow the reply verbatim, as a model would, without interpreting host flags.
            followed = data(call(field(action, 'tool'), **field(action, 'arguments')))
            if field(followed, 'state') == 'pending':
                followed = complete(follow_id)
            assert field(followed, 'state') == 'succeeded', followed
            detailed = data(call('operation_status', operationId=follow_id, details=True))
            assert field(field(detailed, 'details'), 'commandReturnedUtc')
            repeated_wait = data(call('wait', **{'for': 'operation:' + follow_id, 'timeoutSeconds': 1}))
            assert field(repeated_wait, 'state') == 'succeeded'
            assert field(repeated_wait, 'terminal') is None
        finally:
            target.unlink()
        reply = call('build', requestId='live-restored', waitSeconds=0)
        restored = complete(operation_id(reply))
        assert field(restored, 'state') == 'succeeded', restored

        project = fixture / 'diagnostics.proj'
        binlog = fixture / 'diagnostics.binlog'
        project.write_text('<Project><Target Name="Build"><Warning Code="LIVE001" Text="Structured warning" />'
                           '<Error Code="LIVE002" Text="Expected structured error" /></Target></Project>')
        generated = subprocess.run(['dotnet', 'msbuild', str(project), '/t:Build', '/nologo',
                                    '/bl:' + str(binlog) + ';ProjectImports=None'], capture_output=True, text=True, timeout=60)
        assert generated.returncode == 1, generated.stdout
        report = data(call('build_diagnostics', _instance=False, binlog=str(binlog)))
        assert field(report, 'eventStreamComplete') and field(report, 'outcome') == 'failed', report
        assert field(report, 'errors') == 1 and field(report, 'warnings') == 1, report

        line = next(i for i, value in enumerate(source.read_text().splitlines(), 1) if 'int total = 0;' in value)
        call('bp_set', file=str(source), line=line, requestId='live-breakpoint')
        launch = call('launch', project='DebugTarget', requestId='live-launch')
        operation = operation_id(launch)
        assert field(complete(operation), 'state') == 'succeeded'
        stopped = json.loads(text(call('wait', timeoutSeconds=20, structured=True)))
        assert stopped['outcome'] in ('event', 'already-stopped')
        call('launch', project='DebugTarget', requestId='live-launch')
        repeated = json.loads(text(call('wait', timeoutSeconds=1, structured=True)))
        assert repeated['outcome'] == 'already-stopped'
        call('operation_status', operationId=operation)

        # A connected client turns off the debugger options that open dialogs mid-debugging.
        options = dte('-Options', ','.join(PROMPTING_OPTIONS)).split()
        assert options == [name + '=False' for name in PROMPTING_OPTIONS], options

        # A source edit while stopped made the next step open a Hot Reload dialog, and the
        # stop in the edited file a stale-source warning; either held every later call.
        try:
            source.write_text(original.replace('mesh.vertices[i] * scale', 'mesh.vertices[i] * scale + 1'))
            time.sleep(3)
            assert 'stopped' in text(call('step'))
            time.sleep(3)
            assert field(data(call('debug_state')), 'dialog') is None
        finally:
            source.write_text(original)

        # A dialog that is open anyway refuses calls instead of holding them.
        opener_log = open(Path(args.output).with_name('dialog-opener.log'), 'w', encoding='utf-8')
        opener = subprocess.Popen(dte_command('-Show', '-Command', 'Debug.AttachtoProcess'), stdout=opener_log,
                                  stderr=subprocess.STDOUT, text=True)
        try:
            deadline = time.monotonic() + 30
            while (dialog := field(data(call('debug_state')), 'dialog')) is None:
                assert time.monotonic() < deadline, 'Attach to Process dialog did not open'
                time.sleep(0.5)
            refused = request('tools/call', dict(name='status', arguments=dict(instance=instance)))
            assert refused.get('isError') and 'was not run' in text(refused), refused
            close_window(int(args.pid), dialog)
            opener.wait(timeout=30)
        finally:
            if opener.poll() is None:
                opener.kill()
            opener_log.close()
        call('status')
        if args.profiles:
            assert 'Collecting capture' in text(call('profile_start', retainRaw=True))
            profile = field(data(call('debug_state')), 'activeProfile')
            capture_id = field(profile, 'captureId')
            assert re.fullmatch(r'[a-z0-9]{6}', capture_id), capture_id
            assert not any(k.lower() == 'collectorsessionid' for k in profile), profile
        call('detach')
        assert field(data(call('debug_state')), 'mode') == 'design'
        if args.profiles:
            stopped = text(call('profile_stop'))
            pending = re.search(r'operation_status ([a-z0-9]{6})', stopped)
            if pending:
                assert field(complete(pending.group(1)), 'state') == 'succeeded'
            else:
                assert 'capture #1' in stopped, stopped
            collector_stopped = True
            child.terminate()
            child.wait(timeout=5)
            connect()
            assert capture_id in text(call('profile_recover', captureId=capture_id))
            call('profile_export', captureId=capture_id, directory=str(fixture / 'export'), includeRaw=True)
            assert list((fixture / 'export').glob('**/*.json'))
        print('Live builds, cancellation, actionable status, reconnect, deduplication, binary diagnostics and debugger checks passed'
              + ('; interrupted profile recovery/export passed.' if args.profiles else '.'), flush=True)
    finally:
        if child:
            try:
                if capture_id and not collector_stopped:
                    if child.poll() is not None:
                        connect()
                    recovery = text(call('profile_recover_stop', captureId=capture_id))
                    pending = re.search(r'operation_status ([a-z0-9]{6})', recovery)
                    if pending:
                        complete(pending.group(1))
            finally:
                child.terminate()
                child.wait(timeout=5)


if __name__ == '__main__':
    main()
