"""Serve a project using the shared store and the adapted manuscript editor."""
import html
import json
import re
from http.server import ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit, parse_qs

import project
import script_server

WEB = Path(__file__).resolve().parent / 'web'


def editor_html(config):
    source = (WEB / 'shell.html').read_text(encoding='utf-8')
    values = {'__PROJECT_CONFIG__': json.dumps(config, ensure_ascii=False).replace('<', '\\u003c'),
              '__PROJECT_TITLE__': html.escape(config['title']), '__EVENT_CATALOG__': '[]'}
    for name, file in {'CSS': 'editor.css', 'MODEL': 'model.js', 'COLLABORATION': 'collaboration.js',
                       'CHART': 'chart.js', 'REVIEW': 'review.js', 'APP': 'app.js'}.items():
        values['__EDITOR_' + name + '__'] = (WEB / file).read_text(encoding='utf-8')
    return re.sub('|'.join(map(re.escape, values)), lambda m: values[m[0]], source)


class Handler(script_server.Handler):
    def do_GET(self):
        try:
            self.local_request()
            parsed = urlsplit(self.path)
            if parsed.path in ('/', '/index.html', '/events.html', '/script-editor.html'):
                body = editor_html(project.configuration(self.server.project_root))
            elif parsed.path == '/documents.html':
                body = (WEB / 'documents.html').read_text(encoding='utf-8')
            elif parsed.path == '/api/outline':
                self.reply(project.outline(self.server.project_root, parse_qs(parsed.query).get('query', [''])[0]))
                return
            else:
                return super().do_GET()
            encoded = body.encode('utf-8')
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.send_header('Content-Length', str(len(encoded)))
            self.send_header('Cache-Control', 'no-store')
            self.send_header('X-Content-Type-Options', 'nosniff')
            self.end_headers()
            self.wfile.write(encoded)
        except (OSError, ValueError) as error:
            self.handle_error(error)


class Server(ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, root, port=0):
        self.project_root = Path(root).resolve()
        self.store = project.open_store(root)
        self.control = None
        super().__init__(('127.0.0.1', port), Handler)


def serve(root, port):
    if not 0 <= port <= 65535:
        raise ValueError('ポート番号は0〜65535を指定してください。')
    from author import reply
    with Server(root, port) as server:
        print(json.dumps(reply('serve', True, '制作GUIを起動しました。Ctrl+Cで停止します。',
                               dict(url=f'http://127.0.0.1:{server.server_port}/', project=str(root))), ensure_ascii=False), flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass
