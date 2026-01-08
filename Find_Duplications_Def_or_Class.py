import os
import re
import ctypes
from multiprocessing import Pool, cpu_count
RED, GREEN, CYAN, YELLOW, RESET = '\033[91m', '\033[92m', '\033[96m', '\033[93m', '\033[0m'

class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long), ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

def center_console():
    try:
        user32 = ctypes.windll.user32
        hwnd = ctypes.windll.kernel32.GetConsoleWindow()
        if not hwnd: return
        rect = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        win_w, win_h = rect.right - rect.left, rect.bottom - rect.top
        sw, sh = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
        user32.SetWindowPos(hwnd, 0, max(0, (sw - win_w) // 2), max(0, (sh - win_h) // 2), 0, 0, 0x0001 | 0x0004)
    except: pass
EXCLUDE_FOLDERS = {'.git', '__pycache__', '.idea', 'venv', 'env', 'node_modules', 'cache'}
PY_REGEX = re.compile(r'^(\s*)(def|class)\s+(.*?)\s*:?\s*$')
PS_REGEX = re.compile(r'^(\s*)(function|class)\s+(.*?)\s*\{?\s*$')

def get_indent_level(line):
    return len(line) - len(line.lstrip())

def analyze_file(filepath):
    """Worker function: Scans a file while tracking class scope."""
    _, ext = os.path.splitext(filepath)
    regex = PY_REGEX if ext == '.py' else PS_REGEX
    found = {}
    duplicates = []
    current_class = "GLOBAL"
    class_indent = -1
    try:
        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            for i, line in enumerate(f, 1):
                stripped = line.strip()
                if not stripped or stripped.startswith(('#', '//')): continue
                match = regex.match(line)
                if match:
                    indent, keyword, signature = match.groups()
                    current_indent = len(indent)
                    clean_sig = " ".join(signature.split())
                    if keyword == 'class':
                        current_class = clean_sig
                        class_indent = current_indent
                    else:
                        if current_indent <= class_indent:
                            current_class = "GLOBAL"
                            class_indent = -1
                    key = (current_class, keyword, clean_sig)
                    if key not in found:
                        found[key] = []
                    found[key].append(i)
        for (scope, kw, sig), lines in found.items():
            if len(lines) > 1:
                duplicates.append({
                    'file': os.path.basename(filepath),
                    'scope': scope,
                    'type': kw.upper(),
                    'signature': sig,
                    'lines': ", ".join(map(str, lines)),
                    'path': os.path.dirname(filepath)
                })
    except Exception:
        pass
    return duplicates

def print_table(title, data, headers):
    if not data:
        print(f"\n{GREEN}{title}: No duplicates found within the same scope.{RESET}")
        return
    print(f"\n{YELLOW}{title}{RESET}")
    widths = [len(h) for h in headers]
    for row in data:
        for i, val in enumerate(row):
            widths[i] = max(widths[i], len(str(val)))
    widths = [min(w + 2, 50) for w in widths]
    h_str = " | ".join(f"{h:^{w}}" for h, w in zip(headers, widths))
    sep = "-" * len(h_str)
    print(sep)
    print(f"{CYAN}{h_str}{RESET}")
    print(sep)
    for row in data:
        formatted_row = []
        for val, w in zip(row, widths):
            v = str(val)
            formatted_row.append(f"{(v[:w-3] + '...') if len(v) > w else v:<{w}}")
        print(" | ".join(formatted_row))
    print(sep)

def main():
    os.system('cls' if os.name == 'nt' else 'clear')
    os.system('mode con: cols=210 lines=50')
    center_console()
    root = os.getcwd()
    files = []
    for r, dirs, f_list in os.walk(root):
        dirs[:] = [d for d in dirs if d.lower() not in EXCLUDE_FOLDERS]
        for f in f_list:
            if f.lower().endswith(('.py', '.ps1')):
                files.append(os.path.join(r, f))
    if not files:
        print("Nothing to scan.")
        return
    print(f"{CYAN}Scanning {len(files)} files for Scope-Specific Duplicates...{RESET}")
    with Pool(processes=cpu_count()) as pool:
        results = pool.map(analyze_file, files)
    flat_data = [item for sublist in results for item in sublist]
    table_rows = [[d['file'], d['scope'], d['type'], d['signature'], d['lines'], d['path'][-40:]] for d in flat_data]
    print_table("SCOPE-AWARE DUPLICATE DETECTION", table_rows, ["File", "Parent Scope", "Type", "Signature", "Lines", "Directory"])
    input(f"\n{CYAN}Done. Press Enter to exit...{RESET}")
if __name__ == "__main__":
    main()