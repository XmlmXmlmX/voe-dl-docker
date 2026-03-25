import os
import sys
import uuid
import time
import threading
import subprocess
from flask import Flask, render_template, request, jsonify, redirect, url_for
from version import VOE_DL_VERSION

app = Flask(__name__)

_APP_DIR = os.path.dirname(os.path.abspath(__file__))
DOWNLOAD_DIR = (
    os.environ.get('DOWNLOAD_DIR')
    or os.environ.get('DOWNLOAD_PATH')
    or os.path.join(_APP_DIR, 'downloads')
)
os.makedirs(DOWNLOAD_DIR, exist_ok=True)

HISTORY_FILE = os.path.join(DOWNLOAD_DIR, 'downloaded_urls.txt')

SCRIPT_PATH = os.path.join(_APP_DIR, 'dl.py')
DOWNLOAD_TIMEOUT = int(os.environ.get('DOWNLOAD_TIMEOUT', 3600))  # seconds

APP_VERSION = os.environ.get('APP_VERSION', 'v1.0.1')

# In-memory job store (sufficient for a single-container deployment)
jobs = {}
jobs_lock = threading.Lock()
history_lock = threading.Lock()


def load_history():
    """Return list of previously successfully downloaded URLs (chronological order)."""
    urls = []
    with history_lock:
        if os.path.exists(HISTORY_FILE):
            with open(HISTORY_FILE, 'r', encoding='utf-8') as f:
                for line in f:
                    line = line.strip()
                    if line and not line.startswith('#'):
                        urls.append(line)
    return urls


def log_url_to_history(url):
    """Append a successfully downloaded URL to the history file."""
    with history_lock:
        with open(HISTORY_FILE, 'a', encoding='utf-8') as f:
            f.write(url + '\n')


def run_download(job_id, url):
    with jobs_lock:
        jobs[job_id]['status'] = 'running'
        jobs[job_id]['started_at'] = time.time()

    proc = None
    try:
        env = {**os.environ, 'VOE_DL_FORCE_DOWNLOAD': '1'}
        proc = subprocess.Popen(
            [sys.executable, SCRIPT_PATH, '-u', url],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            cwd=DOWNLOAD_DIR,
            env=env,
        )

        log_lines = []
        deadline = time.time() + DOWNLOAD_TIMEOUT
        for line in proc.stdout:
            if time.time() > deadline:
                proc.kill()
                raise subprocess.TimeoutExpired(proc.args, DOWNLOAD_TIMEOUT)
            stripped = line.rstrip('\n')
            log_lines.append(stripped)
            with jobs_lock:
                jobs[job_id]['logs'] = '\n'.join(log_lines)
                # Extract title from dl.py output as soon as it is resolved
                for prefix in ('Name of file: ', 'Using default file name: '):
                    if stripped.startswith(prefix):
                        jobs[job_id]['title'] = stripped[len(prefix):]
                        break

        proc.wait()
        with jobs_lock:
            jobs[job_id]['returncode'] = proc.returncode
            jobs[job_id]['status'] = 'done' if proc.returncode == 0 else 'error'
        if proc.returncode == 0:
            log_url_to_history(url)
    except subprocess.TimeoutExpired:
        with jobs_lock:
            jobs[job_id]['status'] = 'error'
            jobs[job_id]['logs'] = (jobs[job_id].get('logs') or '') + \
                f'\nDownload timed out after {DOWNLOAD_TIMEOUT} seconds.'
    except Exception as exc:
        with jobs_lock:
            jobs[job_id]['status'] = 'error'
            jobs[job_id]['logs'] = (jobs[job_id].get('logs') or '') + '\n' + str(exc)

    with jobs_lock:
        jobs[job_id]['finished_at'] = time.time()


@app.route('/')
def index():
    with jobs_lock:
        job_list = sorted(jobs.values(), key=lambda j: j['created_at'], reverse=True)
    history = load_history()
    return render_template('index.html', jobs=job_list,
                           voe_dl_version=VOE_DL_VERSION,
                           app_version=APP_VERSION,
                           history=history)


@app.route('/download', methods=['POST'])
def start_download():
    urls_raw = request.form.get('urls', '').strip()
    if not urls_raw:
        return redirect(url_for('index'), 303)

    urls = [
        line.strip()
        for line in urls_raw.splitlines()
        if line.strip() and not line.strip().startswith('#')
    ]

    for url in urls:
        job_id = str(uuid.uuid4())
        with jobs_lock:
            jobs[job_id] = {
                'id': job_id,
                'url': url,
                'title': None,
                'status': 'pending',
                'created_at': time.time(),
                'started_at': None,
                'finished_at': None,
                'logs': '',
            }
        t = threading.Thread(target=run_download, args=(job_id, url), daemon=True)
        t.start()

    return redirect(url_for('index'), 303)


@app.route('/history')
def get_history():
    return jsonify(load_history())


@app.route('/jobs')
def get_jobs():
    with jobs_lock:
        job_list = sorted(jobs.values(), key=lambda j: j['created_at'], reverse=True)
    return jsonify(job_list)


@app.route('/status/<job_id>')
def get_status(job_id):
    with jobs_lock:
        job = jobs.get(job_id)
    if not job:
        return jsonify({'error': 'Job not found'}), 404
    return jsonify(job)


if __name__ == '__main__':
    host = os.environ.get('HOST', '0.0.0.0')
    port = int(os.environ.get('PORT', 5000))
    debug = os.environ.get('FLASK_DEBUG', '0') == '1'
    print(f'voe-dl {VOE_DL_VERSION}  |  voe-dl-docker {APP_VERSION}')
    app.run(host=host, port=port, debug=debug)
