#!/usr/bin/env python3
"""Agent-facing CLI. stdout always contains one versioned JSON result."""
import argparse
import json
import sqlite3
import sys
from pathlib import Path

import project
import review_rules
from script_store import Conflict, Invalid, RuleViolation, identify, validate, changes


def field(description, required=False, kind='string', **extra):
    return dict(type=kind, description=description, required=required, **extra)


EVENT = field('台本のuidまたは一意なイベントID', True)
REVISION = field('show/checkoutで取得した更新番号', True, 'integer')
INPUT = field('取得結果を編集したJSONファイル。標準入力は -', True)
COMMANDS = {
    'commands': ('操作一覧と用途を取得', False, {}),
    'describe': ('操作の入力仕様・出力仕様・実行条件を取得', False, {'command': field('操作名', True)}),
    'init': ('空の制作プロジェクトを作成', True, {'title': field('作品名', True)}),
    'show': ('共有原稿・コメント・ルールと更新番号を取得', False, {}),
    'new': ('白紙の台本を作成', True, {'title': field('台本名', True), 'route': field('project.jsonで定義したルートID'), 'source-id': field('任意のイベントID'), 'revision': REVISION}),
    'checkout': ('台本と適用ルール・過去の指摘を取得', False, {'event': EVENT}),
    'save': ('更新番号を照合して編集した原稿を保存', True, {'input': INPUT, 'message': field('変更理由'), 'dry-run': field('保存せず差分・ルールを確認', kind='boolean')}),
    'import': ('旧AC形式などの原稿JSONで原稿全体を置き換え（コメント・履歴は保持）', True, {'input': INPUT, 'revision': REVISION, 'dry-run': field('保存せず差分・ルールを確認', kind='boolean')}),
    'verdict': ('執筆状況と独立した判定タグを変更', True, {'event': EVENT, 'value': field('判定', True, choices=['', 'good', 'fix', 'hold', 'reject']), 'revision': REVISION}),
    'comments': ('台本に付いた添削と返信を取得', False, {'event': EVENT}),
    'comment': ('台本または行へ添削コメント・返信を追加', True, {'event': EVENT, 'text': field('コメント本文', True), 'row': field('行ID。省略時は台本全体'), 'reply': field('返信元コメントID'), 'revision': REVISION}),
    'resolve': ('コメントを解決済みにする、または再開する', True, {'id': field('コメントID', True), 'reopen': field('未解決へ戻す', kind='boolean')}),
    'history': ('変更履歴を取得', False, {'event': field('台本ID。省略時は全体'), 'actor': field('編集者', choices=['human', 'agent']), 'before': field('この履歴番号より前を取得', kind='integer')}),
    'entry': ('履歴の変更前後と保存時点の原稿を取得', False, {'id': field('履歴番号', True, 'integer')}),
    'restore': ('指定した台本を過去の状態へ戻し新たな履歴を記録', True, {'event': EVENT, 'entry': field('履歴番号', True, 'integer'), 'revision': REVISION}),
    'rules': ('表記ルール・注意事項を取得', False, {'event': field('台本ID。省略時は全ルール')}),
    'propose-rule': ('表記ルールを確認待ちとして提案', True, {'input': INPUT}),
    'check': ('台本の空欄・分岐先・未接続行・表記ルールを検査', False, {'event': field('台本ID。省略時は全体')}),
    'outline': ('outline/のMarkdown本文・見出し・行番号を取得', False, {'query': field('全文検索語')}),
    'chart': ('台本の接続をノード・エッジとして取得。条件メモの成否は評価しない', False, {'event': EVENT}),
    'export': ('原稿JSONまたは履歴・コメント込みのSQLiteバックアップを書き出す', True, {'output': field('新規出力ファイル', True), 'format': field('出力形式', choices=['json', 'backup'])}),
    'serve': ('このプロジェクトの制作GUIをlocalhostで起動（Ctrl+Cで停止）', False, {'port': field('ポート番号。0は自動選択、既定8765', kind='integer')}),
}


class Parser(argparse.ArgumentParser):
    def error(self, message):
        raise Invalid(message)


def schema(name):
    if name not in COMMANDS:
        raise Invalid('未知のコマンド: ' + name)
    purpose, mutates, fields = COMMANDS[name]
    return dict(command=name, purpose=purpose, mutates=mutates, parameters=fields,
                globalParameters={'project': '制作プロジェクトのパス（既定は作業ディレクトリ）', 'name': '履歴の表示名。編集者区分は常にagent'},
                preconditions=[] if name in ('commands', 'describe', 'init') else ['init済みのproject.jsonと.authoring/scripts.sqlite3'],
                resultSchema={'schema_version': 1, 'command': 'string', 'ok': 'boolean', 'summary': 'string', 'data': 'object or array or null', 'issues': 'array', 'next_actions': 'array'},
                example=['python3', 'tools/authoring/author.py', '--project', './my-story', name] + [v for k, f in fields.items() if f['required'] for v in ('--' + k, '<' + k + '>')])


def action(root, name, reason, **params):
    argv = [sys.executable, str(Path(__file__).resolve()), '--project', str(root), name]
    for key, value in params.items():
        argv.extend(['--' + key.replace('_', '-'), str(value)])
    return dict(reason=reason, argv=argv, mutates=COMMANDS[name][1])


def reply(command, ok, summary, data=None, issues=None, actions=None):
    return dict(schema_version=1, command=command, ok=ok, summary=summary, data=data,
                issues=issues or [], next_actions=actions or [])


def read_input(path):
    value = json.loads(sys.stdin.read() if path == '-' else Path(path).read_text(encoding='utf-8'))
    if isinstance(value, dict) and value.get('schema_version') == 1 and 'data' in value:
        if value.get('ok') is not True:
            raise Invalid('失敗したコマンドの応答は入力にできません。')
        value = value['data']
    return value


def save_document(store, document, revision, name, message, dry_run=False):
    document = validate(document)
    before = store.read()
    if type(revision) is not int or revision < 0:
        raise Invalid('取得時のrevisionが必要です。')
    if revision != before['revision']:
        raise Conflict(before['revision'])
    diff = changes(before['document'], document)
    ids = {c['eventUid'] for c in diff}
    issues = [issue for event in document['events'] if event['uid'] in ids
              for issue in review_rules.inspect(event, before['rules'])['issues']]
    if issues:
        raise RuleViolation(issues)
    if dry_run:
        return dict(revision=revision, changes=diff, dryRun=True, written=False)
    after = store.save(document, revision, 'agent', name, message)
    return dict(revision=after['revision'], changes=diff, written=bool(diff))


def run(argv):
    globals_parser = Parser(add_help=False)
    globals_parser.add_argument('--project', default='.')
    globals_parser.add_argument('--name', default='Codex')
    globals_parser.add_argument('--json', action='store_true')
    args, rest = globals_parser.parse_known_args(argv)
    root = Path(args.project).resolve()
    command = rest[0] if rest else 'commands'
    if command in ('-h', '--help'):
        command = 'commands'
        rest = []
    if command not in COMMANDS:
        raise Invalid('未知のコマンド: ' + command)
    if '--help' in rest or '-h' in rest:
        return reply(command, True, '入力仕様', schema(command)), 0
    parser = Parser(add_help=False)
    for key, f in COMMANDS[command][2].items():
        kwargs = dict(required=f['required'])
        if f['type'] == 'boolean':
            kwargs['action'] = 'store_true'
        else:
            kwargs['type'] = int if f['type'] == 'integer' else str
            if 'choices' in f:
                kwargs['choices'] = f['choices']
        parser.add_argument('--' + key, **kwargs)
    options = vars(parser.parse_args(rest[1:]))
    next_actions = [action(root, 'commands', '利用できる操作を確認する')]
    if command == 'commands':
        data = [dict(command=k, purpose=v[0], mutates=v[1]) for k, v in COMMANDS.items()]
        next_actions = [action(root, 'describe', '操作の引数と実行条件を取得する', command='init')]
    elif command == 'describe':
        data = schema(options['command'])
    elif command == 'init':
        data = project.initialize(root, options['title'])
        next_actions = [action(root, 'describe', '台本を作る操作を確認する', command='new'), action(root, 'serve', '制作GUIで執筆する')]
    elif command == 'serve':
        from server import serve
        serve(root, 8765 if options['port'] is None else options['port'])
        return None, 0
    else:
        store = project.open_store(root)
        snapshot = store.read()
        event = None
        if options.get('event'):
            target_document = snapshot['document']
            if command == 'restore':
                target_document = store.entry(options['entry']).get('snapshot')
                if target_document is None:
                    raise Invalid('復元できる台本の履歴ではありません。')
            event = identify(target_document, options['event'])
        next_actions = [action(root, 'check', '保存済みの台本を検査する')]
        if command == 'show':
            data = snapshot
        elif command == 'new':
            config = project.configuration(root)
            route = options['route'] or next(iter(config['routes']))
            if route not in config['routes']:
                raise Invalid('project.json の routes にルートを登録してください。')
            event = project.new_event(options['title'], route, options['source_id'] or '')
            snapshot['document']['events'].append(event)
            data = save_document(store, snapshot['document'], options['revision'], args.name, '台本を作成')
            data['event'] = event
            next_actions = [action(root, 'checkout', '台本と執筆前の注意事項を取得する', event=event['uid'])]
        elif command == 'checkout':
            data = dict(format='lom-script-checkout', revision=snapshot['revision'], event=event,
                        reviewContext=review_rules.context(snapshot, event))
            next_actions = [action(root, 'describe', '取得JSONを編集して保存するための入力仕様を確認する', command='save')]
        elif command in ('save', 'import'):
            payload = read_input(options['input'])
            if not isinstance(payload, dict):
                raise Invalid('原稿JSONはオブジェクトにしてください。')
            revision = options.get('revision') if command == 'import' else payload.get('revision')
            if payload.get('format') == 'lom-script-checkout':
                updated = payload.get('event')
                if not isinstance(updated, dict):
                    raise Invalid('event がありません。')
                old = identify(snapshot['document'], updated.get('uid'))
                document = snapshot['document']
                document['events'][document['events'].index(old)] = updated
            else:
                document = payload.get('document', payload)
            data = save_document(store, document, revision, args.name, options.get('message') or '原稿を保存', options['dry_run'])
        elif command == 'verdict':
            event['verdict'] = options['value']
            data = save_document(store, snapshot['document'], options['revision'], args.name, '判定タグを変更')
        elif command == 'comments':
            data = [c for c in snapshot['comments'] if c['event_uid'] == event['uid']]
        elif command == 'comment':
            updated = store.comment(options['revision'], event['uid'], options['text'], 'agent', args.name,
                                    options['row'] or '', options['reply'] or '')
            data = dict(revision=updated['revision'], commentId=updated['commentId'], cursor=updated['cursor'])
            next_actions = [action(root, 'comments', '保存したコメントを確認する', event=event['uid'])]
        elif command == 'resolve':
            data = dict(cursor=store.resolve(options['id'], not options['reopen'], 'agent', args.name)['cursor'])
        elif command == 'history':
            data = store.history(event['uid'] if event else '', options['actor'] or '', options['before'] or 0)
        elif command == 'entry':
            data = store.entry(options['id'])
        elif command == 'restore':
            updated = store.restore(options['revision'], options['entry'], event['uid'], 'agent', args.name)
            data = dict(revision=updated['revision'])
        elif command == 'rules':
            data = [r for r in snapshot['rules'] if event is None or review_rules.applies(r, event)]
        elif command == 'propose-rule':
            data = store.rule(read_input(options['input']), 'agent', args.name)['rules']
        elif command == 'check':
            issues, reminders = [], []
            for item in [event] if event else snapshot['document']['events']:
                issues.extend(project.inspect(item))
                report = review_rules.inspect(item, snapshot['rules'])
                issues.extend(dict(code='REVIEW_RULE_VIOLATION', severity='error', **i) for i in report['issues'])
                reminders.extend(report['reminders'])
            ok = not any(i['severity'] == 'error' for i in issues)
            actions = [action(root, 'checkout', '問題の台本と指摘を取得して修正する', event=i) for i in dict.fromkeys(x['eventUid'] for x in issues)]
            return reply(command, ok, '検査完了', dict(reminders=reminders, gameValidated=False), issues, actions), 0 if ok else 1
        elif command == 'outline':
            data = project.outline(root, options['query'] or '')
        elif command == 'chart':
            data = project.graph(event)
        elif command == 'export':
            output = Path(options['output']).resolve()
            if output.exists():
                raise Invalid('出力ファイルが存在します。別の名前を指定してください。')
            if options['format'] == 'backup':
                store.backup(output)
            else:
                with output.open('x', encoding='utf-8') as stream:
                    json.dump(snapshot['document'], stream, ensure_ascii=False, indent=2)
            data = dict(output=str(output), includesHistory=options['format'] == 'backup', gamePackage=False)
    return reply(command, True, COMMANDS[command][0], data, actions=next_actions), 0


def main():
    argv = sys.argv[1:]
    # Parse only for error context; never trust it as an instruction to write.
    command = next((a for a in argv if a in COMMANDS), 'commands')
    root = Path.cwd()
    try:
        if '--project' in argv:
            root = Path(argv[argv.index('--project') + 1]).resolve()
        result, code = run(argv)
    except (Invalid, Conflict, json.JSONDecodeError, OSError, sqlite3.Error, ValueError, TypeError, KeyError, IndexError) as error:
        issue = dict(code='INVALID_INPUT', message=str(error))
        data = None
        actions = [action(root, 'describe', '入力仕様を確認する', command=command)]
        code = 2
        if isinstance(error, Conflict):
            issue['code'] = 'REVISION_CONFLICT'
            data = dict(currentRevision=error.revision)
            actions = [action(root, 'show', '最新版を取得し、変更内容を比較してから編集をやり直す')]
            code = 3
        elif isinstance(error, RuleViolation):
            issue['code'] = 'REVIEW_RULE_VIOLATION'
            data = dict(violations=error.issues)
            actions = [action(root, 'rules', '確定した表記ルールを確認する')]
            code = 4
        elif isinstance(error, (OSError, sqlite3.Error)):
            issue['code'] = 'IO_ERROR'
        result = reply(command, False, '操作を完了できませんでした', data, [issue], actions)
    if result is not None:
        print(json.dumps(result, ensure_ascii=False))
    return code


if __name__ == '__main__':
    sys.exit(main())
