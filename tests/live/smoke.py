"""Real stdio MCP checks, restricted to the fixture's dedicated VS instance."""
import argparse
import json
import os
from pathlib import Path
import queue
import re
import subprocess
import threading
import time


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
    instance = workspace['name'] + '#' + args.pid
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
    def connect():
        nonlocal child
        child = subprocess.Popen([args.shim, '--cwd', str(fixture)], stdin=subprocess.PIPE,
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, env=env)
        threading.Thread(target=receive, args=(child,), daemon=True).start()
        threading.Thread(target=child.stderr.read, daemon=True).start()
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

    def data(response):
        value = response.get('structuredContent')
        if value is None:
            value = json.loads(text(response))
        return value

    def field(value, key):
        return next((v for k, v in value.items() if k.lower() == key.lower()), None)

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
            operation = re.search(r'build-[0-9a-f]+', text(reply)).group()
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
            failed = complete(re.search(r'build-[0-9a-f]+', text(reply)).group())
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
            cancel_id = re.search(r'build-[0-9a-f]+', text(reply)).group()
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
            follow_id = re.search(r'build-[0-9a-f]+', text(reply)).group()
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
        restored = complete(re.search(r'build-[0-9a-f]+', text(reply)).group())
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
        operation = re.search(r'launch-[0-9a-f]+', text(launch)).group()
        assert field(complete(operation), 'state') == 'succeeded'
        stopped = json.loads(text(call('wait', timeoutSeconds=20, structured=True)))
        assert stopped['outcome'] in ('event', 'already-stopped')
        call('launch', project='DebugTarget', requestId='live-launch')
        repeated = json.loads(text(call('wait', timeoutSeconds=1, structured=True)))
        assert repeated['outcome'] == 'already-stopped'
        call('operation_status', operationId=operation)
        if args.profiles:
            assert 'Collecting capture' in text(call('profile_start', retainRaw=True))
            profile = field(data(call('debug_state')), 'activeProfile')
            capture_id = field(profile, 'captureId')
            assert capture_id
        call('detach')
        assert field(data(call('debug_state')), 'mode') == 'design'
        if args.profiles:
            stopped = text(call('profile_stop'))
            pending = re.search(r'profile-stop-[0-9a-f]+', stopped)
            if pending:
                assert field(complete(pending.group()), 'state') == 'succeeded'
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
                    pending = re.search(r'profile-stop-[0-9a-f]+', recovery)
                    if pending:
                        complete(pending.group())
            finally:
                child.terminate()
                child.wait(timeout=5)


if __name__ == '__main__':
    main()
