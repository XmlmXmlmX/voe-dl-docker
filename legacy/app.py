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
_raw_download_dir = (
    os.environ.get('DOWNLOAD_DIR')
    or os.environ.get('DOWNLOAD_PATH')
    or '/downloads'
)
# Resolve to an absolute path so that subprocess(cwd=...) always gets an
# absolute directory regardless of what the caller passed in.
DOWNLOAD_DIR = (
    _raw_download_dir if os.path.isabs(_raw_download_dir)
    else os.path.abspath(_raw_download_dir)
)
os.makedirs(DOWNLOAD_DIR, exist_ok=True)

HISTORY_FILE = os.path.join(DOWNLOAD_DIR, 'downloaded_urls.txt')

SCRIPT_PATH = os.path.join(_APP_DIR, 'dl.py')
DOWNLOAD_TIMEOUT = int(os.environ.get('DOWNLOAD_TIMEOUT', 3600))  # seconds

APP_VERSION = os.environ.get('APP_VERSION', 'dev')

# In-memory job store (sufficient for a single-container deployment)
jobs = {}
job_procs = {}  # job_id -> subprocess.Popen (not serialised to JSON)
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
        # Abort was requested before the thread even started the subprocess
        if jobs[job_id]['status'] == 'aborted':
            jobs[job_id]['finished_at'] = time.time()
            return
        jobs[job_id]['status'] = 'running'
        jobs[job_id]['started_at'] = time.time()

    proc = None
    try:
        env = {**os.environ, 'VOE_DL_FORCE_DOWNLOAD': '1', 'DOWNLOAD_DIR': DOWNLOAD_DIR}
        proc = subprocess.Popen(
            [sys.executable, SCRIPT_PATH, '-u', url],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            cwd=DOWNLOAD_DIR,
            env=env,
        )

        with jobs_lock:
            job_procs[job_id] = proc

        log_lines = []
        deadline = time.time() + DOWNLOAD_TIMEOUT
        for line in proc.stdout:
            if time.time() > deadline:
                proc.kill()
                raise subprocess.TimeoutExpired(proc.args, DOWNLOAD_TIMEOUT)
            # Check if abort was requested while we were reading output
            with jobs_lock:
                if jobs[job_id]['status'] == 'aborted':
                    proc.kill()
                    break
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
            if jobs[job_id]['status'] != 'aborted':
                jobs[job_id]['status'] = 'done' if proc.returncode == 0 else 'error'
        if proc.returncode == 0:
            log_url_to_history(url)
    except subprocess.TimeoutExpired:
        with jobs_lock:
            if jobs[job_id]['status'] != 'aborted':
                jobs[job_id]['status'] = 'error'
            jobs[job_id]['logs'] = (jobs[job_id].get('logs') or '') + \
                f'\nDownload timed out after {DOWNLOAD_TIMEOUT} seconds.'
    except Exception as exc:
        with jobs_lock:
            if jobs[job_id]['status'] != 'aborted':
                jobs[job_id]['status'] = 'error'
            jobs[job_id]['logs'] = (jobs[job_id].get('logs') or '') + '\n' + str(exc)
    finally:
        with jobs_lock:
            job_procs.pop(job_id, None)
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


@app.route('/abort/<job_id>', methods=['POST'])
def abort_job(job_id):
    with jobs_lock:
        job = jobs.get(job_id)
        if not job:
            return jsonify({'error': 'Job not found'}), 404
        if job['status'] not in ('pending', 'running'):
            return jsonify({'error': 'Job is not running'}), 400
        job['status'] = 'aborted'
        proc = job_procs.pop(job_id, None)
    if proc:
        try:
            proc.kill()
        except Exception:
            pass
    return jsonify({'id': job_id, 'status': 'aborted'})


@app.route('/requeue/<job_id>', methods=['POST'])
def requeue_job(job_id):
    with jobs_lock:
        job = jobs.get(job_id)
        if not job:
            return jsonify({'error': 'Job not found'}), 404
        if job['status'] not in ('done', 'error', 'aborted'):
            return jsonify({'error': 'Job is not finished'}), 400
        url = job['url']

    new_job_id = str(uuid.uuid4())
    with jobs_lock:
        jobs[new_job_id] = {
            'id': new_job_id,
            'url': url,
            'status': 'pending',
            'created_at': time.time(),
            'started_at': None,
            'finished_at': None,
            'logs': '',
        }
    t = threading.Thread(target=run_download, args=(new_job_id, url), daemon=True)
    t.start()
    return jsonify({'id': new_job_id, 'status': 'pending'})


if __name__ == '__main__':
    host = os.environ.get('HOST', '0.0.0.0')
    port = int(os.environ.get('PORT', 5000))
    debug = os.environ.get('FLASK_DEBUG', '0') == '1'
    print(f'voe-dl {VOE_DL_VERSION}  |  voe-dl-docker {APP_VERSION}')
    app.run(host=host, port=port, debug=debug)
