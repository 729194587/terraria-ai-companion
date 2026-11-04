"""Bounded local task evidence. Export is passive: it never replays game actions."""
from contextvars import ContextVar
from datetime import datetime, timezone
import argparse
import json
from pathlib import Path
import re
import uuid
import zipfile

import agent_trace as tracing

current = ContextVar('task_evidence', default=None)
DEFAULT_ROOT = Path(__file__).resolve().parents[1] / '.local' / 'evidence'
FILES = re.compile(r'(manifest\.json|events\.jsonl|source-[0-9a-f]{64}\.lua)\Z')


class Bundle:
    """At most 2 MiB of events and 64 bounded sources; keep 32 finished tasks."""
    def __init__(self, root=None, *, maximum=32, max_bytes=2*1024*1024, metadata=None):
        self.root = Path(root) if root is not None else DEFAULT_ROOT
        self.id = uuid.uuid4().hex
        self.path = self.root / self.id
        self.maximum, self.max_bytes = maximum, max_bytes
        self.size = self.sequence = self.dropped = self.write_errors = 0
        self.sources = {}
        self.manifest = dict(schemaVersion=1, taskId=self.id, status='running',
                             startedAt=datetime.now(timezone.utc).isoformat(), context=metadata or {})
        self.enabled = True
        try:
            self.path.mkdir(parents=True, exist_ok=False)
            self._manifest()
        except OSError:
            self.enabled = False
            self.write_errors += 1

    def _manifest(self):
        if not self.enabled:
            return
        data = {**self.manifest, 'eventBytes': self.size, 'eventsDropped': self.dropped,
                'writeErrors': self.write_errors, 'sources': list(self.sources.values()),
                'replay': 'not supported; observations do not recreate game physics or server state'}
        raw = json.dumps(tracing.trace._redact(data), ensure_ascii=False, allow_nan=False)
        temporary = self.path / 'manifest.tmp'
        temporary.write_text(raw, encoding='utf-8')
        temporary.replace(self.path / 'manifest.json')

    def record(self, event, **data):
        if not self.enabled:
            return
        try:
            self.sequence += 1
            record = {'type':event, 'timestamp':datetime.now(timezone.utc).isoformat(),
                      'sequence':self.sequence, **tracing.context.get(), **data}
            raw = (json.dumps(tracing.trace._redact(record), ensure_ascii=False, allow_nan=False)+'\n').encode('utf-8')
            oversized=len(raw)>65536
            if oversized:
                self.dropped += 1
                marker={k:record[k] for k in ('task_id','lua_run_id','lua_call_id','lua_line','source_sha256',
                    'generation','chat_event_id','tool_call_id','model_request_id','connection_id','request_id','sequence','timestamp') if k in record}
                raw=(json.dumps(tracing.trace._redact({**marker,'type':'evidence_record_dropped',
                    'original_type':event,'bytes':len(raw),'reason':'event_limit'}),ensure_ascii=False)+'\n').encode('utf-8')
            if len(raw)>65536 or self.size+len(raw)>self.max_bytes:
                if not oversized:
                    self.dropped += 1
                return
            with (self.path/'events.jsonl').open('ab') as stream:
                offset=stream.tell()
                try:
                    if stream.write(raw)!=len(raw):
                        raise OSError('Short evidence write')
                except OSError:
                    stream.truncate(offset)
                    raise
            self.size += len(raw)
        except Exception:
            self.write_errors += 1

    def source(self, source):
        import hashlib
        digest = hashlib.sha256(source.encode('utf-8')).hexdigest()
        if not self.enabled or digest in self.sources:
            return digest
        try:
            if len(self.sources)>=64 or len(source.encode('utf-8'))>16384:
                self.dropped += 1
                return digest
            saved = tracing.trace._redact(source)
            if len(saved.encode('utf-8'))>16384:
                self.dropped += 1
                self.sources[digest]=dict(sha256=digest, omitted=True, reason='redacted_source_limit')
                self._manifest()
                return digest
            filename='source-'+digest+'.lua'
            (self.path/filename).write_text(saved, encoding='utf-8')
            self.sources[digest]=dict(sha256=digest, file=filename, redacted=saved!=source)
            self._manifest()
        except Exception:
            self.write_errors += 1
        return digest

    def finish(self, **result):
        self.manifest.update(result, finished=True, finishedAt=datetime.now(timezone.utc).isoformat())
        try:
            self._manifest()
            self._prune()
        except Exception:
            self.write_errors += 1

    def _prune(self):
        # Only our completed UUID directories and known files are eligible. Unknown
        # user-added files, symlinks, active tasks and paths outside root are retained.
        root=self.root.resolve()
        tasks=[]
        for folder in self.root.iterdir():
            if not re.fullmatch('[0-9a-f]{32}',folder.name) or folder.is_symlink() or not folder.is_dir():
                continue
            if folder.resolve().parent!=root:
                continue
            try:
                state=json.loads((folder/'manifest.json').read_text(encoding='utf-8'))
                files=list(folder.iterdir())
                if state.get('finished') is not True or any(f.is_symlink() or not f.is_file() or not FILES.fullmatch(f.name) for f in files):
                    continue
                tasks.append((folder.stat().st_mtime_ns,folder,files))
            except (OSError,ValueError):
                continue
        for _,folder,files in sorted(tasks,reverse=True)[self.maximum:]:
            for file in files:
                if file.resolve().parent!=folder.resolve():
                    raise ValueError('Evidence file escaped task directory')
                file.unlink()
            folder.rmdir()

    def receipt(self):
        return dict(taskId=self.id, path=str(self.path.resolve()), available=self.enabled,
                    eventsDropped=self.dropped, writeErrors=self.write_errors)


def record(event, **data):
    bundle=current.get()
    if bundle is not None:
        bundle.record(event, **data)


def export(directory, output):
    directory=Path(directory).resolve()
    output=Path(output).resolve()
    if not (directory/'manifest.json').is_file():
        raise ValueError('Not a task evidence directory')
    if json.loads((directory/'manifest.json').read_text(encoding='utf-8')).get('finished') is not True:
        raise ValueError('Export after task cleanup finishes')
    files=list(directory.iterdir())
    selected=[]
    for file in files:
        if FILES.fullmatch(file.name) and not file.is_symlink() and file.is_file() and file.resolve().parent==directory:
            selected.append(file)
    if len(selected)>66 or sum(f.stat().st_size for f in selected)>4*1024*1024:
        raise ValueError('Evidence export exceeds bounded task size')
    # Never overwrite a previous export or any evidence input.
    with zipfile.ZipFile(output,'x',compression=zipfile.ZIP_DEFLATED) as archive:
        for file in sorted(selected):
            archive.write(file, arcname=file.name)
    return output


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory',type=Path)
    parser.add_argument('output',type=Path)
    args=parser.parse_args()
    print(export(args.directory,args.output))


if __name__=='__main__':
    main()
