"""Literal proofreading rules shared by the store, CLI and style checker."""
FIELDS = {'all', 'text', 'speaker', 'expression', 'metadata'}
SCOPES = {'project', 'event', 'speaker'}
DEFINITION = ('kind', 'bad', 'good', 'reason', 'scope', 'eventUid', 'speaker', 'field', 'match', 'status', 'commentId')


def definition(raw):
    if not isinstance(raw, dict):
        raise ValueError('ルールの形式が不正です。')
    result = {}
    defaults = dict(kind='literal', bad='', good='', reason='', scope='event', eventUid='', speaker='',
                    field='text', match='contains', status='active', commentId='')
    for key in DEFINITION:
        value = raw.get(key, defaults[key])
        if not isinstance(value, str) or len(value) > (20000 if key == 'reason' else 1000):
            raise ValueError('ルールの入力値が不正です: ' + key)
        result[key] = value
    if (result['kind'] not in {'literal', 'reminder'} or result['scope'] not in SCOPES
            or result['field'] not in FIELDS or result['match'] not in {'contains', 'exact'}
            or result['status'] not in {'active', 'proposed', 'disabled'}):
        raise ValueError('ルールの種類・範囲・状態が不正です。')
    if result['scope'] == 'event' and not result['eventUid']:
        raise ValueError('適用する台本を指定してください。')
    if result['scope'] == 'speaker' and not result['speaker'].strip():
        raise ValueError('適用する話者名を指定してください。')
    if result['scope'] == 'speaker' and result['field'] == 'metadata' and result['kind'] == 'literal':
        raise ValueError('話者限定では本文・話者名・表情を検査対象にしてください。')
    if result['kind'] == 'literal':
        if not result['bad'].strip() or not result['good'].strip() or result['bad'] == result['good']:
            raise ValueError('NG表記と異なる推奨表記を入力してください。')
    elif not result['reason'].strip():
        raise ValueError('次回も確認する注意事項を入力してください。')
    if result['scope'] != 'event': result['eventUid'] = ''
    if result['scope'] != 'speaker': result['speaker'] = ''
    if result['kind'] == 'reminder': result['bad'] = result['good'] = ''
    return result


def applies(rule, event):
    if rule['scope'] == 'event': return rule['eventUid'] == event.get('uid')
    if rule['scope'] == 'speaker':
        return any(r.get('type') == 'dialogue' and r.get('speaker') == rule['speaker'] for r in event.get('rows', []))
    return True


def matches(text, rule):
    bad, good = rule['bad'], rule['good']
    if not bad: return False
    if rule['match'] == 'exact': return text == bad
    # If the preferred spelling contains the short NG string, don't flag that spelling.
    if not good or bad not in good: return bad in text
    preferred = text.find(good)
    start = 0
    while (start := text.find(bad, start)) >= 0:
        while preferred >= 0 and preferred + len(good) < start + len(bad):
            preferred = text.find(good, preferred + 1)
        if preferred < 0 or preferred > start: return True
        start += 1
    return False


def inspect(event, rules):
    issues, reminders = [], []
    for rule in rules:
        if rule['status'] != 'active' or not applies(rule, event): continue
        if rule['kind'] == 'reminder':
            reminders.append(rule); continue
        fields = []
        if rule['field'] in {'all', 'metadata'} and rule['scope'] != 'speaker':
            fields.extend(('', 0, key, '', event.get(key, '')) for key in ('title', 'place', 'period'))
        for number, row in enumerate(event.get('rows', []), 1):
            if rule['scope'] == 'speaker' and (row.get('type') != 'dialogue' or row.get('speaker') != rule['speaker']): continue
            if rule['field'] in {'all', 'text'}:
                fields.append((row['id'], number, 'text', '', row.get('text', '')))
                fields.extend((row['id'], number, 'option', o.get('id', ''), o.get('text', '')) for o in row.get('options', []))
            for field in ('speaker', 'expression'):
                if rule['field'] in {'all', field} and row.get('type') == 'dialogue':
                    fields.append((row['id'], number, field, '', row.get(field, '')))
        for row_id, number, field, option_id, value in fields:
            if matches(value, rule):
                issues.append(dict(ruleId=rule['id'], eventUid=event.get('uid', ''), sourceId=event.get('sourceId', ''),
                    rowId=row_id, rowNumber=number, field=field, optionId=option_id, bad=rule['bad'], good=rule['good'],
                    reason=rule['reason'], commentId=rule['commentId'], text=value))
    return dict(issues=issues, reminders=reminders)


def context(snapshot, event):
    return dict(
        instruction='執筆前に確定ルール・注意事項・ユーザーの指摘を読み、イベント全体を点検する。コメントの解決済みは今回の対応完了であり、指摘の撤回ではない。userCommentsには他台本の指摘も含む。出典の台本・文脈を確かめ、未確定の案を全体ルールへ一般化しない。最新のユーザー指示とルールの状態を優先し、確認待ちを確定扱いせず、無効のルールは適用しない。',
        eventUid=event['uid'],
        rules=[r for r in snapshot.get('rules', []) if applies(r, event)],
        userComments=[c for c in snapshot['comments'] if c['actor'] == 'human'],
        checks=inspect(event, snapshot.get('rules', [])))
