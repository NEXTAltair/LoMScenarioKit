"""Run with uv run --with playwright python tools/authoring/tests/browser.py.

All project data is temporary. Set AUTHORING_CHROMIUM to an installed browser,
and AUTHORING_SCREENSHOT to retain a screenshot for review.
"""
import json
import os
import subprocess
import sys
import tempfile
import threading
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from project import initialize
from server import Server
from playwright.sync_api import sync_playwright, expect


def main():
    with tempfile.TemporaryDirectory(prefix='lom-authoring-browser-') as directory:
        root = Path(directory)
        initialize(root, 'シナリオ制作テスト')
        config = json.loads((root / 'project.json').read_text())
        config.update(routes={'north': '北方編'}, speakers=['旅人'])
        (root / 'project.json').write_text(json.dumps(config))
        (root / 'outline' / 'setting.md').write_text('# 世界設定\n旅人の針路\n## 出会い\n<script>window.injected=true</script>\n')
        server = Server(root)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        base = f'http://127.0.0.1:{server.server_port}'
        cli = [sys.executable, '-B', str(Path(__file__).resolve().parents[1] / 'author.py'), '--project', str(root)]
        def command(*args):
            result = subprocess.run(cli + list(args), capture_output=True, text=True, check=True)
            return json.loads(result.stdout)['data']
        errors = []
        try:
            with sync_playwright() as p:
                browser = p.chromium.launch(executable_path=os.environ.get('AUTHORING_CHROMIUM'), headless=True)
                page = browser.new_page(viewport={'width': 1440, 'height': 1000})
                page.on('pageerror', lambda e: errors.append(str(e)))
                page.goto(base)
                page.locator('#new-button').click()
                page.locator('#event-title').fill('港での出会い')
                page.locator('[data-type="narration"] .row-text').fill('旅人は港へ向かった。')
                page.locator('[data-type="narration"] [data-action="type"]').select_option('dialogue')
                converted = page.locator('[data-type="dialogue"]').first
                expect(converted.locator('[data-field="speaker"]')).to_have_value('旅人')
                converted.locator('[data-action="type"]').select_option('narration')
                page.locator('[data-type="dialogue"] .row-text').fill('ここが港か。')
                page.locator('#event-verdict').select_option('fix')
                page.locator('#save-button').click()
                expect(page.locator('#save-state')).to_contain_text('共有保存済み')
                event = server.store.read()['document']['events'][0]
                assert event['verdict'] == 'fix' and event['route'] == 'north'
                uid, rid = event['uid'], event['rows'][1]['id']
                page.locator('[data-type="dialogue"] [data-action="comment"]').click()
                page.locator('#comment-text').fill('この台詞を短く。')
                page.locator('#post-comment').click()
                expect(page.locator('.comment-card')).to_have_count(1)
                payload = command('checkout', '--event', uid)
                assert payload['reviewContext']['userComments'][0]['text'] == 'この台詞を短く。'
                payload['event']['rows'][1]['text'] = '港だ。'
                edit = root / 'edit.json'; edit.write_text(json.dumps(payload))
                command('save', '--input', str(edit))
                expect(page.locator('[data-type="dialogue"] .row-text')).to_have_value('港だ。', timeout=10000)
                revision = command('show')['revision']
                command('comment', '--event', uid, '--row', rid, '--revision', str(revision), '--text', '短くしました。')
                expect(page.locator('.comment-card')).to_have_count(2, timeout=10000)
                page.locator('.comment-card').first.locator('[data-resolve]').click()
                expect(page.locator('.comment-card.resolved')).to_have_count(1)
                page.locator('#chart-view-tab').click()
                expect(page.locator('#chart-canvas')).to_contain_text('港だ。')
                assert command('chart', '--event', uid)['conditionsEvaluated'] is False
                if os.environ.get('AUTHORING_SCREENSHOT'):
                    page.screenshot(path=os.environ['AUTHORING_SCREENSHOT'], full_page=True)
                page.locator('#verdict-filter').select_option('good')
                expect(page.locator('.draft-item')).to_have_count(0)
                page.locator('#verdict-filter').select_option('fix')
                expect(page.locator('.draft-item')).to_have_count(1)
                page.goto(base + '/documents.html')
                page.locator('#search').fill('針路')
                page.locator('#documents button').click()
                expect(page.locator('#toc button')).to_have_count(2)
                page.locator('#toc button').last.click()
                expect(page.locator('#body')).to_contain_text('<script>')
                assert not page.evaluate('Boolean(window.injected)')
                # Reopening the same project preserves the shared verdict.
                page.goto(base)
                expect(page.locator('#event-verdict')).to_have_value('fix')
                assert errors == [], errors
                browser.close()
        finally:
            server.shutdown(); server.server_close(); thread.join()
    print('Browser checks passed: custom project, verdict, human/agent edits, comments, chart, outline and safe text rendering.')


if __name__ == '__main__':
    main()
