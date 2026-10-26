"""Bound the pinned server queue; fail the build if the reviewed source changes."""
import argparse
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--source', type=Path, default=Path('/opt/kev/kev/serve.py'),
                    help='Pinned serving source; override for a native checkout.')
path = parser.parse_args().source
source = path.read_text()
patches = {
    'self.queue, self.stopping = queue.Queue(), threading.Event()': 'self.queue, self.stopping = queue.Queue(maxsize=4), threading.Event()',
    '        self.queue.put((enc, done))': '        try: self.queue.put_nowait((enc, done))\n        except queue.Full: raise HTTPException(429, "decision_queue_full")',
    'uvicorn.run(app, host=a.host, port=a.port)': 'uvicorn.run(app, host=a.host, port=a.port, limit_concurrency=8, timeout_graceful_shutdown=10)',
}
for before, after in patches.items():
    if source.count(before) != 1:
        raise SystemExit('Pinned Kev serving source differs from reviewed hardening patch.')
    source = source.replace(before, after)
path.write_text(source)
