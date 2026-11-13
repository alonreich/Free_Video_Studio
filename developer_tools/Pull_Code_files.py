import sys
import os

# ══════════════════════════════════════════════════════════════════════════════════════════
# NO CACHE. BOTH LINES, BEFORE EVERY OTHER IMPORT. This file previously had NEITHER guard.
# Running it directly was safe (Python does not cache the __main__ module), but importing it
# from anywhere would have dropped a __pycache__ beside it. `dont_write_bytecode` must sit above
# the imports because it only governs imports made after it; the env var covers child processes.
# ══════════════════════════════════════════════════════════════════════════════════════════
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'


# Governed by docs/05_SYSTEM_LIFECYCLE_STORAGE.md#SYS-CODEEXPORT.
# CODEEXPORT_01: complete accounting, no deletion, no silent decode failures.
import argparse
import codecs
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timezone
import fnmatch
import hashlib
import math
import re
import stat
import subprocess
import uuid
from pathlib import Path

MAX_CODE_FILES = 8  # + manifest + inventory = at most ten attachments per export.
MIN_PART_CHARS = 500_000
IGNORED_DIRS = {
    '.git', 'bin', 'obj', '.vs', '.idea', 'node_modules', 'compile', 'compiled',
    'old_code', 'artifacts', 'packages', 'testresults', 'venv', '.venv', 'env',
    '.pytest_cache', '__pycache__', 'claude outputs', '.devlogs', '.fvs_xfer',
    '.gemini', 'backup', '.ssh', '.aws', '.azure', 'avregression',
    'logs',  # TESTOUT_01: every test result lives in logs\<Suite>_Results; opt in with --test-result.
}
SOURCE_EXTENSIONS = {
    '.cs', '.axaml', '.cmd', '.bat', '.ps1', '.py', '.json', '.json5', '.xml',
    '.csproj', '.sln', '.slnx', '.txt', '.md', '.svg', '.manifest', '.config',
    '.props', '.targets', '.yaml', '.yml', '.razor', '.resx', '.xaml', '.css',
    '.js', '.ts', '.tsx', '.jsx', '.html', '.htm', '.ini', '.toml', '.ruleset',
    '.iss', '.sh', '.sql', '.csv', '.trx', '.log',
}
SOURCE_NAMES = {
    '.gitignore', '.gitattributes', '.editorconfig', '.agignore', '.clineignore',
    '.codexignore', '.geminiignore', 'license', 'dockerfile',
}
ASSET_EXTENSIONS = {
    '.ico', '.png', '.jpg', '.jpeg', '.gif', '.mp3', '.mp4', '.wav', '.dll',
    '.exe', '.traineddata', '.bmp', '.webp', '.zip', '.7z', '.pdb', '.cer', '.pem',
}
CONFIG_EXTENSIONS = {
    '.json', '.json5', '.xml', '.csproj', '.sln', '.slnx', '.config', '.props',
    '.targets', '.ini', '.toml', '.ruleset', '.manifest', '.yaml', '.yml',
}
START_HERE = ['AGENTS.md', 'docs/README.md', 'docs/SPEC_GOVERNANCE.md', 'docs/INDEX.md']
# Verified existing attribution: 0x97 Windows em dashes. No global encoding guess.
LEGACY_ENCODINGS = {'mp3/!_System Recall - Aftertune (No CopyRights).txt': 'cp1252'}
MODEL_GUIDANCE = (
    'TARGET: advanced online GUI models, including GPT-6 Astra with max reasoning.\n'
    'Select the model/reasoning in your GUI; this offline exporter cannot set them.\n'
    'GUI context, per-file upload limits and retrieval behavior: NOT VERIFIED for your account.\n'
    'API context specifications are NOT used as GUI upload/context limits.\n'
    'Max reasoning is not evidence that all attachments fit or were read.\n'
    'Ten attachments is this exporter\'s requested cap, not a provider-wide limit.\n'
    'Token figures are rough UTF-8-bytes/4 estimates, NOT tokenizer measurements or guarantees.\n'
    'Conversation, system instructions, reasoning and response need additional capacity.\n'
    'If a confirmed GUI budget is supplied, comparisons are advisory estimates only.\n'
    'No files are silently dropped to meet a guessed model budget.\n'
    'Official references checked 2026-10-19 (recheck when the GUI changes):\n'
    'https://learn.chatgpt.com/docs/models\n'
    'https://developers.openai.com/api/docs/models/gpt-6-astra (API only)\n'
    'https://developers.openai.com/api/docs/guides/reasoning (API only)\n'
)
TOKEN_RE = re.compile(
    r'\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,}|AIza[A-Za-z0-9_-]{30,}'
    r'|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}'
    r'|AKIA[A-Z0-9]{16}|xox[baprs]-[A-Za-z0-9-]{16,}'
    r'|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})\b'
)
PRIVATE_KEY_RE = re.compile(
    r'-----BEGIN (?:[A-Z0-9 ]*PRIVATE KEY)-----.*?'
    r'(?:-----END [A-Z0-9 ]*PRIVATE KEY-----|\Z)', re.DOTALL
)
ASSIGNMENT_RE = re.compile(
    r'''(?ix)(?P<prefix>["']?\b[\w.-]*(?:api[_-]?key|password|passwd|secret|token)["']?\s*[:=]\s*)(?P<quote>["'])(?P<value>[^\r\n]*?)(?P=quote)'''
)
BEARER_RE = re.compile(r'(?i)\b(Bearer\s+)[A-Za-z0-9._~+/-]{16,}=*')
URL_AUTH_RE = re.compile(r'(https?://)[^\s/:@]+:[^\s/@]+@', re.IGNORECASE)


def redact_sensitive(text):
    # Defense in depth; cannot prove that arbitrary secrets have been recognized.
    count = 0
    for pattern in (PRIVATE_KEY_RE, TOKEN_RE, BEARER_RE, URL_AUTH_RE):
        text, n = pattern.subn('[REDACTED]', text)
        count += n

    def assignment(match):
        nonlocal count
        value = match['value']
        if not value or value.startswith((chr(36) + '{', '{{', '[REDACTED]')) or value.lower() in {
            'example', 'placeholder', 'your_api_key', 'your-api-key', 'changeme',
        }:
            return match[0]
        count += 1
        return match['prefix'] + match['quote'] + '[REDACTED]' + match['quote']

    text = ASSIGNMENT_RE.sub(assignment, text)
    text, n = re.subn(
        r'(?im)((?:^\s*(?:export\s+)?|;\s*)(?:[\w.-]*(?:api[_-]?key|password|passwd|secret|access[_-]?token))\s*=\s*)([^\s;"\'<>]+)',
        lambda m: m[1] + '[REDACTED]', text,
    )
    return text, count + n


def get_downloads_directory():
    return Path(os.environ.get('USERPROFILE', str(Path.home()))) / 'Downloads'


def is_secret_or_sensitive(path):
    name = path.name.lower()
    return (name.endswith(('.password.txt', '.pfx', '.p12', '.key.pem', '.key', '.jks'))
            or (name.startswith('.env') and not name.endswith('.example'))
            or name in {'credentials', '.netrc', '.npmrc', 'id_rsa', 'id_ed25519',
                        'credentials.json', 'secrets.json'})


def is_link(path):
    info = path.lstat()
    return path.is_symlink() or bool(getattr(info, 'st_file_attributes', 0)
                                    & getattr(stat, 'FILE_ATTRIBUTE_REPARSE_POINT', 0))


def decode_text(data):
    # Strict BOM-aware decoding: never silently replace characters.
    for bom, encoding in ((codecs.BOM_UTF32_LE, 'utf-32'), (codecs.BOM_UTF32_BE, 'utf-32'),
                          (codecs.BOM_UTF16_LE, 'utf-16'), (codecs.BOM_UTF16_BE, 'utf-16'),
                          (codecs.BOM_UTF8, 'utf-8-sig')):
        if data.startswith(bom):
            return data.decode(encoding), encoding
    if b'\x00' in data:
        sample = data[:4096]
        if len(data) % 2 == 0 and len(sample) >= 4:
            for offset, encoding in ((1, 'utf-16-le'), (0, 'utf-16-be')):
                lane = sample[offset::2]
                if lane.count(0) / len(lane) > 0.6:
                    return data.decode(encoding), encoding
        raise UnicodeError('NUL-containing text without a recognized encoding')
    return data.decode('utf-8'), 'utf-8'


def get_group_name(file_path, project_root):
    parts = tuple(p.lower() for p in file_path.relative_to(project_root).parts)
    ext = file_path.suffix.lower()
    if parts[0] == 'tests':
        return '06_Tests'
    if parts[0] == 'docs' or ext == '.md' or file_path.name == 'project_structure.txt':
        return '00_Specifications_and_Architecture'
    if parts[:2] == ('src', 'freevideostudio.core') and ext == '.cs':
        return '01_Core_Logic'
    if parts[:2] == ('src', 'freevideostudio.app') and ext == '.cs':
        return '02_App_Code'
    if ext in {'.axaml', '.xaml', '.css'}:
        return '03_UI_Markup'
    if parts[0] in {'build', 'developer_tools', 'tools', 'ssl-certificate'} or ext in {'.cmd', '.bat'}:
        return '05_Developer_Tools_and_Build'
    if parts[0] == '.github' or ext in CONFIG_EXTENSIONS or file_path.name.lower() in SOURCE_NAMES:
        return '04_Configuration'
    if ext in ASSET_EXTENSIONS:
        return '07_Assets_and_Binaries'
    return '08_Misc'


@dataclass
class Entry:
    path: str
    status: str
    reason: str = ''
    size: int = 0
    text: str = ''
    encoding: str = ''
    digest: str = ''
    group: str = ''
    bundle: str = '-'
    line: int = 0


def read_entry(path, label, group, legacy_encoding=None):
    entry = Entry(label, 'TEXT', group=group)
    encoding_origin = 'user-selected' if legacy_encoding else 'repository-declared'
    legacy_encoding = legacy_encoding or LEGACY_ENCODINGS.get(label)
    try:
        if is_link(path):
            entry.status, entry.reason = 'SKIPPED', 'link/reparse point; target not read'
            return entry
        if is_secret_or_sensitive(path):
            entry.status, entry.reason = 'SKIPPED', 'sensitive filename; contents not read'
            return entry
        before = path.stat()
        entry.size = before.st_size
        ext = path.suffix.lower()
        if ext in ASSET_EXTENSIONS:
            entry.status, entry.reason = 'METADATA', 'asset/certificate; contents not exported'
            return entry
        if ext not in SOURCE_EXTENSIONS and path.name.lower() not in SOURCE_NAMES:
            entry.status, entry.reason = 'METADATA', 'unrecognized format; review if needed'
            return entry
        data = path.read_bytes()
        after = path.stat()
        if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
            raise OSError('file changed during read')
        try:
            text, entry.encoding = decode_text(data)
        except UnicodeError:
            if not legacy_encoding or b'\x00' in data:
                raise
            text = data.decode(legacy_encoding, errors='strict')
            entry.encoding = legacy_encoding + f' ({encoding_origin} fallback; verify original encoding)'
        entry.text, redactions = redact_sensitive(text)
        if redactions:
            entry.status, entry.reason = 'REDACTED', f'{redactions} credential pattern(s) removed; review locally'
        entry.digest = hashlib.sha256(entry.text.encode('utf-8')).hexdigest()
    except (OSError, UnicodeError) as exc:
        entry.status, entry.reason = 'ERROR', type(exc).__name__ + '; contents unavailable'
        entry.text = ''
    return entry


def git_snapshot(root):
    def git(*args):
        try:
            result = subprocess.run(['git', '-C', str(root), *args], capture_output=True,
                                    timeout=15, check=True)
            return result.stdout.decode('utf-8', errors='strict').strip()
        except (OSError, UnicodeError, subprocess.SubprocessError):
            return None
    head = git('rev-parse', 'HEAD')
    status = git('status', '--porcelain=v1', '--untracked-files=all')
    tracked = git('ls-files', '-z')
    return {'commit': head or 'unavailable', 'status': status,
            'tracked': [p for p in tracked.split('\0') if p] if tracked else [],
            'available': head is not None and status is not None and tracked is not None}


def collect_entries(root, focus=(), legacy_encoding=None):
    entries = []
    def onerror(error):
        try:
            label = Path(error.filename).relative_to(root).as_posix() + '/'
        except (ValueError, TypeError):
            label = '[unreadable directory]'
        entries.append(Entry(label, 'ERROR', 'directory enumeration failed'))
    for current, dirs, files in os.walk(root, followlinks=False, onerror=onerror):
        for name in sorted(dirs, key=str.casefold):
            path = Path(current) / name
            reason = ''
            try:
                if is_link(path):
                    reason = 'link/reparse point; subtree not read'
                elif name.lower() in IGNORED_DIRS:
                    reason = 'excluded generated/private/history directory; entire subtree skipped'
            except OSError:
                reason = 'directory inaccessible; subtree not read'
            if reason:
                dirs.remove(name)
                entries.append(Entry(path.relative_to(root).as_posix() + '/',
                                     'ERROR' if reason.startswith('directory inaccessible') else 'SKIPPED', reason))
        dirs.sort(key=str.casefold)
        for name in sorted(files, key=str.casefold):
            path = Path(current) / name
            rel = path.relative_to(root).as_posix()
            group = get_group_name(path, root)
            if name.lower() == 'build.log':
                entries.append(Entry(rel, 'SKIPPED', 'local log; opt in with --test-result'))
                continue
            keep = (not focus or group in {'00_Specifications_and_Architecture', '04_Configuration', '06_Tests'}
                    or path.suffix.lower() in CONFIG_EXTENSIONS
                    or any(fnmatch.fnmatchcase(rel.lower(), f.lower()) for f in focus))
            if not keep:
                entries.append(Entry(rel, 'SKIPPED', 'outside explicit focus; dependencies may be absent'))
                continue
            entries.append(read_entry(path, rel, group, legacy_encoding))
    return sorted(entries, key=lambda e: e.path.casefold())


def pack_into_capped_parts(items, max_parts=MAX_CODE_FILES):
    if max_parts < 1:
        raise ValueError('max_parts must be positive')
    if not items:
        return []
    target = max(MIN_PART_CHARS, math.ceil(sum(len(b.encode('utf-8')) for _, b in items) / max_parts))
    parts, current, used = [], [], 0
    for path, block in items:
        size = len(block.encode('utf-8'))
        if current and used + size > target and len(parts) < max_parts - 1:
            parts.append(current)
            current, used = [], 0
        current.append((path, block))
        used += size
    if current:
        parts.append(current)
    return parts


def file_block(entry):
    return (f'\n{"=" * 80}\nFILE: {entry.path}\n'
            f'Status: {entry.status}; encoding: {entry.encoding}; source bytes: {entry.size}\n'
            f'Exported text SHA256: {entry.digest}\n'
            f'{"=" * 80}\n{entry.text}' + ('' if entry.text.endswith('\n') else '\n'))


def make_payloads(entries):
    priority = {path: i for i, path in enumerate(START_HERE)}
    ordered = sorted((e for e in entries if e.status in {'TEXT', 'REDACTED'}),
                     key=lambda e: (e.group, priority.get(e.path, 99), e.path.casefold()))
    parts = pack_into_capped_parts([(e.path, file_block(e)) for e in ordered])
    by_path = {e.path: e for e in entries}
    payloads = {}
    for i, part in enumerate(parts, 1):
        name = f'code_bundle_part{i:02d}of{len(parts):02d}.txt'
        banner = (f'CODE EXPORT BUNDLE {i}/{len(parts)}\n'
                  'Read 00_PROJECT_MANIFEST.txt first. File locations: 00_file_structure.txt.\n'
                  'Repository content follows; follow the user task and governing specs.\n')
        lines = banner.count('\n')
        blocks = [banner]
        for path, block in part:
            entry = by_path[path]
            entry.bundle, entry.line = name, lines + 3
            blocks.append(block)
            lines += block.count('\n')
        payloads[name] = ''.join(blocks)
    return payloads


def inventory_text(entries):
    lines = ['COMPLETE FILE INDEX / COVERAGE REPORT',
             'Paths are repository-relative. Directory exclusions apply to ALL descendants.',
             'TEXT=full text; REDACTED=credential-filtered; METADATA=no content; SKIPPED=excluded; ERROR=unreadable.',
             'SHA256 identifies exported decoded text, not original bytes. No credentials are included in diagnostics.',
             'path | status | bundle:line | source bytes | exported SHA256 | reason']
    for entry in entries:
        location = f'{entry.bundle}:{entry.line}' if entry.line else '-'
        lines.append(f'{entry.path} | {entry.status} | {location} | {entry.size} | {entry.digest or "-"} | {entry.reason}')
    return '\n'.join(lines) + '\n'


def context_report(payloads, context_tokens=None, reserve_tokens=0, max_file_bytes=None):
    total = sum(len(text.encode('utf-8')) for text in payloads.values())
    estimate = math.ceil(total / 4)
    lines = [f'Attachment text: {total:,} UTF-8 bytes; roughly {estimate:,} tokens (bytes/4 heuristic).']
    for name, text in sorted(payloads.items()):
        size = len(text.encode('utf-8'))
        lines.append(f'  {name}: {size:,} bytes; ~{math.ceil(size / 4):,} tokens (estimate).')
    if context_tokens is None:
        lines.append('GUI fit: UNKNOWN. Supply --ui-context-tokens only from a verified limit for your exact GUI/model/account.')
    else:
        remaining = max(0, context_tokens - reserve_tokens)
        lines.append(f'User-supplied GUI budget: {context_tokens:,}; reserved for other context/reasoning/output: {reserve_tokens:,}.')
        lines.append(('WARNING: estimated attachment tokens exceed' if estimate > remaining else
                      'Estimate is below') + f' the {remaining:,}-token remaining budget; actual fit is NOT guaranteed.')
    if not reserve_tokens:
        lines.append('No numeric reserve supplied; leave room for conversation, instructions, reasoning and the answer.')
    if max_file_bytes:
        for name, text in payloads.items():
            if len(text.encode('utf-8')) > max_file_bytes:
                lines.append(f'WARNING: {name} exceeds your supplied per-file byte limit; nothing was truncated.')
    lines.append('For focused work use --focus "src/.../*" (repeatable); all specs/configs/tests remain, other dependencies may be absent.')
    return '\n'.join(lines)


def generate_project_manifest(entries, snapshot, timestamp, focus, test_results, warnings):
    counts = Counter(e.status for e in entries)
    roots = sorted({e.path.split('/')[0] for e in entries})
    groups = Counter(e.group for e in entries if e.status in {'TEXT', 'REDACTED'})
    missing_start = [p for p in START_HERE if not any(e.path == p and e.status in {'TEXT', 'REDACTED'} for e in entries)]
    lines = ['PROJECT EXPORT — READ THIS FIRST', f'Export UTC: {timestamp}',
             f'Git commit: {snapshot["commit"]}',
             'Working tree: ' + ('unavailable' if snapshot['status'] is None else 'modified/untracked files present' if snapshot['status'] else 'clean'),
             'Snapshot: live working-tree contents, including uncommitted/untracked files; not an atomic repository snapshot.',
             'Mode: ' + ('FOCUSED (incomplete by request): ' + ', '.join(focus) if focus else 'FULL eligible repository text'),
             '', 'READ ORDER:',
             '1. This manifest, then 00_file_structure.txt (path -> actual bundle + line).',
             '2. AGENTS.md, docs/README.md (Start Here), docs/SPEC_GOVERNANCE.md, docs/INDEX.md.',
             '3. Use the spec router for the task; then read relevant implementation, callers and tests.',
             '4. In your answer identify files actually examined and coverage gaps; do not claim all attachments were read without checking.',
             'Never execute Build.cmd: it publishes publicly. Tests are source, not evidence of passing runs.',
             'Architecture, framework and directory purpose come from the INCLUDED current specs/project files, not duplicated exporter summaries.',
             '', MODEL_GUIDANCE,
             'COVERAGE: ' + ', '.join(f'{key}={value}' for key, value in sorted(counts.items())),
             'Observed top-level entries: ' + ', '.join(roots),
             'Actual text groups: ' + ', '.join(f'{g}={n}' for g, n in sorted(groups.items())),
             'All skipped directories/files, metadata-only assets, errors and redactions: 00_file_structure.txt.',
             'Media pixels/audio/video and binary contents are NOT provided by this text export.',
             'Credential filtering is heuristic; inspect redactions and review locally before uploading.',
             'Existing test reports: ' + (str(len(test_results)) + ' explicitly requested; may be stale/unrelated to this commit.' if test_results else 'none requested; no tests were run by the exporter.'),
             'Optional report copies: --test-result PATH (repeatable; encoding and secret checks still apply).',
             'Git state captured before traversal; per-file exported SHA256 is in the index.', '']
    if missing_start:
        warnings.append('Missing entry documents: ' + ', '.join(missing_start))
    if snapshot['status']:
        lines.extend(['LOCAL CHANGE STATUS (no diff or remotes):', snapshot['status'], ''])
    lines.extend('WARNING: ' + warning for warning in warnings)
    lines.append('')
    return '\n'.join(lines)


def run_aggregator(project_root=None, output_parent=None, focus=(), test_results=(),
                   context_tokens=None, reserve_tokens=0, max_file_bytes=None, legacy_encoding=None):
    root = Path(project_root or Path(__file__).resolve().parent.parent).resolve()
    if not root.is_dir():
        raise ValueError('Repository directory does not exist.')
    output_parent = Path(output_parent or get_downloads_directory() / 'free_video_studio').resolve()
    if output_parent == root or root in output_parent.parents:
        raise ValueError('Output must be outside the repository to prevent recursive exports.')
    if context_tokens is not None and (context_tokens <= 0 or reserve_tokens >= context_tokens):
        raise ValueError('Reserve must be smaller than the positive GUI context budget.')
    if reserve_tokens < 0 or (max_file_bytes is not None and max_file_bytes <= 0):
        raise ValueError('Invalid reserve or file limit.')
    if legacy_encoding:
        codecs.lookup(legacy_encoding)
    timestamp = datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')
    snapshot = git_snapshot(root)
    entries = collect_entries(root, focus, legacy_encoding)
    known = {e.path for e in entries}
    skipped_dirs = [e for e in entries if e.path.endswith('/')]
    for tracked in snapshot['tracked']:
        if tracked and tracked not in known:
            parent = next((e for e in skipped_dirs if tracked.startswith(e.path)), None)
            entries.append(Entry(tracked, 'SKIPPED' if parent else 'ERROR',
                                 f'under {parent.path}: {parent.reason}' if parent else 'tracked file absent from traversal (possibly locally deleted)'))
    for i, report in enumerate(test_results, 1):
        path = Path(report).absolute()
        entry = read_entry(path, f'[test-results]/{i}_{path.name}', '06_Tests', legacy_encoding)
        if entry.status == 'TEXT':
            entry.reason = 'user-selected existing result; does not prove this snapshot passes'
        entries.append(entry)
    entries.sort(key=lambda e: e.path.casefold())
    warnings = []
    legacy_count = sum('fallback' in e.encoding for e in entries)
    if legacy_count:
        warnings.append(f'{legacy_count} file(s) used declared legacy encodings; exact encoding and its origin are in each file header.')
    if not snapshot['available']:
        warnings.append('Git metadata unavailable; tracked-file completeness could not be checked.')
    after = git_snapshot(root)
    if (snapshot['commit'], snapshot['status']) != (after['commit'], after['status']):
        warnings.append('Git state changed during export; rerun after edits settle.')
    error_count = sum(e.status == 'ERROR' for e in entries)
    if error_count:
        warnings.append(f'INCOMPLETE: {error_count} read/coverage errors; see index. Exit status 2.')
    for entry in entries:
        entry.path = redact_sensitive(entry.path)[0]
        entry.reason = redact_sensitive(entry.reason)[0]
    payloads = make_payloads(entries)
    payloads['00_file_structure.txt'] = inventory_text(entries)
    manifest = generate_project_manifest(entries, snapshot, timestamp, focus, test_results, warnings)
    manifest = redact_sensitive(manifest)[0]
    report = ''
    for _ in range(12):
        payloads['00_PROJECT_MANIFEST.txt'] = manifest + '\nUPLOAD SIZE REPORT\n' + report + '\n'
        next_report = context_report(payloads, context_tokens, reserve_tokens, max_file_bytes)
        if next_report == report:
            break
        report = next_report
    payloads['00_PROJECT_MANIFEST.txt'] = manifest + '\nUPLOAD SIZE REPORT\n' + report + '\n'
    output_parent.mkdir(parents=True, exist_ok=True)
    destination = output_parent / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S_%fZ') + '_' + uuid.uuid4().hex[:8])
    destination.mkdir(exist_ok=False)  # No deletion, overwrite, or bytecode purge.
    for name in sorted(payloads, key=lambda n: (n == '00_PROJECT_MANIFEST.txt', n)):
        with (destination / name).open('x', encoding='utf-8', newline='\n') as handle:
            handle.write(payloads[name])
    print(f'Export: {destination}\nUpload ONLY the {len(payloads)} .txt files in this folder (maximum 10).')
    print(report)
    print('Coverage: ' + ', '.join(f'{k}={v}' for k, v in sorted(Counter(e.status for e in entries).items())))
    print('Previous exports preserved. Review the coverage/redaction index before uploading.')
    return destination, (2 if error_count else 0)


def main(argv=None):
    parser = argparse.ArgumentParser(description='Export repository context into <=10 files for advanced online AI GUIs. Offline; preserves older exports.')
    parser.add_argument('--output-parent', type=Path, help='Parent for a NEW dated folder; default: ~/Downloads/free_video_studio')
    parser.add_argument('--focus', action='append', default=[], help='Optional repository-relative glob; repeatable. Keeps all docs/configs/tests.')
    parser.add_argument('--test-result', type=Path, action='append', default=[], help='Include an existing report; never runs tests.')
    parser.add_argument('--ui-context-tokens', type=int, help='Confirmed context budget for your specific GUI/account; no assumed API limit.')
    parser.add_argument('--reserve-tokens', type=int, default=0, help='Capacity YOU reserve for conversation, instructions, reasoning and response.')
    parser.add_argument('--max-file-bytes', type=int, help='Confirmed GUI per-file byte cap; warn without truncation.')
    parser.add_argument('--legacy-encoding', help='Explicit fallback for non-UTF text, e.g. cp1252; strict decoding, recorded in each file header.')
    args = parser.parse_args(argv)
    try:
        _, status = run_aggregator(output_parent=args.output_parent, focus=args.focus,
                                  test_results=args.test_result, context_tokens=args.ui_context_tokens,
                                  reserve_tokens=args.reserve_tokens, max_file_bytes=args.max_file_bytes,
                                  legacy_encoding=args.legacy_encoding)
        return status
    except (OSError, ValueError, LookupError) as exc:
        print(f'Export FAILED ({type(exc).__name__}); no previous exports were changed.', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
