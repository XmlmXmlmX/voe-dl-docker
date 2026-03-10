import os
import sys
import uuid
import time
import threading
import subprocess
from flask import Flask, render_template, request, jsonify, redirect, url_for

app = Flask(__name__)

_APP_DIR = os.path.dirname(os.path.abspath(__file__))
DOWNLOAD_DIR = os.environ.get('DOWNLOAD_DIR', os.path.join(_APP_DIR, 'downloads'))
os.makedirs(DOWNLOAD_DIR, exist_ok=True)

SCRIPT_PATH = os.path.join(_APP_DIR, 'dl.py')
DOWNLOAD_TIMEOUT = int(os.environ.get('DOWNLOAD_TIMEOUT', 3600))  # seconds

# In-memory job store (sufficient for a single-container deployment)
jobs = {}
jobs_lock = threading.Lock()


def run_download(job_id, url):
    with jobs_lock:
        jobs[job_id]['status'] = 'running'
        jobs[job_id]['started_at'] = time.time()

    try:
        env = {**os.environ, 'VOE_DL_FORCE_DOWNLOAD': '1'}
        proc = subprocess.run(
            [sys.executable, SCRIPT_PATH, '-u', url],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            cwd=DOWNLOAD_DIR,
            env=env,
            timeout=DOWNLOAD_TIMEOUT,
        )
        with jobs_lock:
            jobs[job_id]['logs'] = proc.stdout.strip()
            jobs[job_id]['returncode'] = proc.returncode
            jobs[job_id]['status'] = 'done' if proc.returncode == 0 else 'error'
    except subprocess.TimeoutExpired:
        with jobs_lock:
            jobs[job_id]['status'] = 'error'
            jobs[job_id]['logs'] = f'Download timed out after {DOWNLOAD_TIMEOUT} seconds.'
    except Exception as exc:
        with jobs_lock:
            jobs[job_id]['status'] = 'error'
            jobs[job_id]['logs'] = str(exc)

    with jobs_lock:
        jobs[job_id]['finished_at'] = time.time()


@app.route('/')
def index():
    with jobs_lock:
        job_list = sorted(jobs.values(), key=lambda j: j['created_at'], reverse=True)
    return render_template('index.html', jobs=job_list)


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
                'status': 'pending',
                'created_at': time.time(),
                'started_at': None,
                'finished_at': None,
                'logs': '',
            }
        t = threading.Thread(target=run_download, args=(job_id, url), daemon=True)
        t.start()

    return redirect(url_for('index'), 303)


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
    app.run(host=host, port=port, debug=debug)
