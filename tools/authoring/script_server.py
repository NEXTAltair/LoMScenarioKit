#!/usr/bin/env python3
"""Serve the local editor and its shared store; browser writes are human-authored."""
import argparse
import json
import secrets
import sqlite3
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

from script_store import DEFAULT_DB, ROOT, Conflict, Invalid, RuleViolation, Store


class EditorServer(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, store, control=None):
        self.store = store
        self.control = control
        super().__init__(address, Handler)


class Handler(BaseHTTPRequestHandler):
    def control_status(self):
        state = self.server.control
        if not state or not secrets.compare_digest(self.headers.get('X-Script-Control', ''), state['token']):
            raise PermissionError('サーバーの操作用コマンドから実行してください。')
        return {key: value for key, value in state.items() if key != 'token'}

    def reply(self, data, status=200):
        body = json.dumps(data, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Content-Type-Options', 'nosniff')
        self.end_headers()
        self.wfile.write(body)

    def local_request(self, mutation=False):
        port = self.server.server_port
        hosts = {f'127.0.0.1:{port}', f'localhost:{port}'}
        host = self.headers.get('Host', '')
        if host not in hosts:
            raise PermissionError('ローカルアドレスから開いてください。')
        if mutation:
            origin = self.headers.get('Origin')
            if origin and origin != 'http://' + host:
                raise PermissionError('別のサイトからの保存は受け付けません。')
            if self.headers.get('Content-Type', '').split(';')[0] != 'application/json' or self.headers.get('X-Script-Editor') != '1':
                raise PermissionError('台本エディタから保存してください。')

    def handle_error(self, error):
        if isinstance(error, RuleViolation):
            self.reply(dict(error=str(error), issues=error.issues), 422)
        elif isinstance(error, Conflict):
            self.reply(dict(error=str(error), revision=error.revision), 409)
        elif isinstance(error, PermissionError):
            self.reply(dict(error=str(error)), 403)
        elif isinstance(error, (Invalid, ValueError, TypeError, KeyError)):
            self.reply(dict(error=str(error)), 400)
        else:
            self.log_error('Storage error: %s', error)
            self.reply(dict(error='保存処理に失敗しました。原稿を書き出してから再試行してください。'), 500)

    def do_GET(self):
        try:
            self.local_request()
            parsed = urlsplit(self.path)
            path, query = parsed.path, parse_qs(parsed.query)
            if path == '/api/server':
                self.reply(self.control_status())
            elif path == '/api/project':
                self.reply(self.server.store.read())
            elif path == '/api/history':
                self.reply(self.server.store.history(query.get('event', [''])[0], query.get('actor', [''])[0], int(query.get('before', ['0'])[0])))
            elif path.startswith('/api/history/'):
                self.reply(self.server.store.entry(int(path.rsplit('/', 1)[-1])))
            elif path == '/api/backup':
                with tempfile.TemporaryDirectory() as directory:
                    target = Path(directory) / 'scripts.sqlite3'
                    self.server.store.backup(target)
                    body = target.read_bytes()
                self.send_response(200)
                self.send_header('Content-Type', 'application/octet-stream')
                self.send_header('Content-Disposition', 'attachment; filename="scripts-backup.sqlite3"')
                self.send_header('Content-Length', str(len(body)))
                self.send_header('Cache-Control', 'no-store')
                self.end_headers(); self.wfile.write(body)
            elif path in ('/', '/index.html', '/events.html', '/script-editor.html'):
                file = ROOT / (path.lstrip('/') or 'script-editor.html')
                if not file.is_file():
                    self.reply(dict(error='python3 scripts/build_html.py でHTMLを生成してください。'), 404); return
                body = file.read_bytes()
                self.send_response(200)
                self.send_header('Content-Type', 'text/html; charset=utf-8')
                self.send_header('Content-Length', str(len(body)))
                self.send_header('Cache-Control', 'no-cache')
                self.send_header('X-Content-Type-Options', 'nosniff')
                self.end_headers(); self.wfile.write(body)
            else:
                self.reply(dict(error='見つかりません。'), 404)
        except (OSError, sqlite3.Error, Invalid, Conflict, ValueError, TypeError, KeyError) as error:
            self.handle_error(error)

    def do_POST(self):
        try:
            self.local_request(mutation=True)
            length = int(self.headers.get('Content-Length', '0'))
            if not 0 < length <= 20 * 1024 * 1024:
                raise Invalid('送信データは20MB以下にしてください。')
            payload = json.loads(self.rfile.read(length))
            if not isinstance(payload, dict):
                raise Invalid('送信形式が不正です。')
            path = urlsplit(self.path).path
            store = self.server.store
            # Identity comes from the entry point, never from a document or request field.
            if path == '/api/server/stop':
                result = self.control_status()
                self.reply(result)
                threading.Thread(target=self.server.shutdown, daemon=True).start()
                return
            elif path == '/api/save':
                result = store.save(payload['document'], payload['revision'], 'human', 'ユーザー', payload.get('message', ''))
            elif path == '/api/comments':
                result = store.comment(payload['revision'], payload['eventUid'], payload['text'], 'human', 'ユーザー', payload.get('rowId', ''), payload.get('parentId', ''))
            elif path == '/api/rules':
                result = store.rule(payload['rule'], 'human', 'ユーザー', payload.get('id', ''), payload.get('version'))
            elif path.startswith('/api/comments/') and path.endswith('/resolve'):
                result = store.resolve(path.split('/')[3], payload['resolved'], 'human', 'ユーザー')
            elif path == '/api/restore':
                result = store.restore(payload['revision'], payload['entryId'], payload['eventUid'], 'human', 'ユーザー')
            else:
                self.reply(dict(error='見つかりません。'), 404); return
            self.reply(result)
        except (OSError, sqlite3.Error, Invalid, Conflict, ValueError, TypeError, KeyError) as error:
            self.handle_error(error)
