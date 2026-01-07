import os
import sys
import tokenize
import re
import ctypes

RED = '\033[91m'
GREEN = '\033[92m'
CYAN = '\033[96m'
RESET = '\033[0m'

class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long), ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

def center_console():
    try:
        user32 = ctypes.windll.user32
        hwnd = ctypes.windll.kernel32.GetConsoleWindow()
        if not hwnd: return
        rect = RECT()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
        win_w = rect.right - rect.left
        win_h = rect.bottom - rect.top
        screen_w = user32.GetSystemMetrics(0)
        screen_h = user32.GetSystemMetrics(1)
        x = max(0, (screen_w - win_w) // 2)
        y = max(0, (screen_h - win_h) // 2)
        user32.SetWindowPos(hwnd, 0, x, y, 0, 0, 0x0001 | 0x0004)
    except: pass

EXCLUDE_FOLDERS = ['.git', '__pycache__', '.idea', 'venv', 'env', 'cache', 'project']
EXCLUDE_FILES = ['__init__.py', 'app.py']
EXCLUDE_EXTS = ['.txt', '.md', '.log', '.json']

def get_target_files(root_dir):
    targets = []
    for root, dirs, files in os.walk(root_dir):
        dirs[:] = [d for d in dirs if d.lower() not in EXCLUDE_FOLDERS]
        for file in files:
            if file in EXCLUDE_FILES: continue
            _, ext = os.path.splitext(file)
            if ext in EXCLUDE_EXTS: continue
            if ext in ['.py', '.ps1']:
                targets.append(os.path.join(root, file))
    return targets

def analyze_comments(filepath):
    items = []
    _, ext = os.path.splitext(filepath)
    if ext not in ['.py', '.ps1']:
        return []
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            lines = f.readlines()
        actions = {} 
        if ext == '.py':
            with open(filepath, 'rb') as f:
                tokens = list(tokenize.tokenize(f.readline))
            for t in tokens:
                if t.type == tokenize.COMMENT:
                    row = t.start[0] - 1
                    col = t.start[1]
                    if col == 0:
                        actions[row] = {'action': 'DELETE', 'type': 'COMMENT', 'line': row + 1, 'content': t.string.strip()}
                    else:
                        if row not in actions:
                            original = lines[row]
                            clean_content = original[:col].rstrip() + '\n'
                            actions[row] = {'action': 'EDIT', 'type': 'INLINE COMMENT', 'line': row + 1, 'content': f"Rem: {t.string.strip()}", 'new_content': clean_content}
        elif ext == '.ps1':
            for i, line in enumerate(lines):
                stripped = line.strip()
                if stripped.startswith('#'):
                    actions[i] = {'action': 'DELETE', 'type': 'COMMENT', 'line': i + 1, 'content': stripped}
        for i in range(len(lines)):
            if i in actions and actions[i]['action'] == 'DELETE':
                continue
            line = lines[i]
            if not line.strip():
                is_duplicate = False
                if i + 1 < len(lines):
                    if not lines[i+1].strip():
                        is_duplicate = True
                if is_duplicate:
                    actions[i] = {'action': 'DELETE', 'type': 'EMPTY LINE', 'line': i + 1, 'content': '<Redundant Empty>'}
                    continue
                next_real_line = None
                for j in range(i + 1, len(lines)):
                    if lines[j].strip():
                        next_real_line = lines[j].strip()
                        break
                if not next_real_line:
                    actions[i] = {'action': 'DELETE', 'type': 'EMPTY LINE', 'line': i + 1, 'content': '<Trailing Empty>'}
        return [v for k, v in sorted(actions.items())]
    except Exception as e:
        return []

def nuke_comments(filepath, items):
    try:
        print(f"\n{CYAN}Cleaning Comments/Empty Lines in: {filepath}{RESET}")
        with open(filepath, 'r', encoding='utf-8') as f:
            lines = f.readlines()
        
        action_map = {item['line'] - 1: item for item in items}
        
        with open(filepath, 'w', encoding='utf-8') as f:
            for i, line in enumerate(lines):
                if i in action_map:
                    act = action_map[i]

                    start_idx = max(0, i - 2)
                    end_idx = min(len(lines), i + 3)
                    
                    print("-" * 40)

                    for ctx in range(start_idx, i):
                        print(f"  {lines[ctx].rstrip()}")

                    print(f"{RED}- {lines[i].rstrip()}{RESET}")
                    
                    if act['action'] == 'DELETE':
                        print(f"{GREEN}+ ### LINE REMOVED ###{RESET}")

                    elif act['action'] == 'EDIT':
                        print(f"{GREEN}+ {act['new_content'].rstrip()}{RESET}")
                        f.write(act['new_content'])

                    for ctx in range(i + 1, end_idx):
                        print(f"  {lines[ctx].rstrip()}")
                        
                else:
                    f.write(line)
        
        print("-" * 40)
        return True
    except Exception as e:
        print(f"Error writing {filepath}: {e}")
        return False

def check_syntax(filepath):
    _, ext = os.path.splitext(filepath)
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            source = f.read()
        if '\t' in source:
             return "Indentation: File contains Tabs"
        if ext == '.py':
            compile(source, filepath, 'exec')
        return None
    except (IndentationError, TabError) as e:
        return f"Indentation Error: {e}"
    except SyntaxError as e:
        return f"Syntax Error: {e}"
    except Exception:
        return None

def fix_syntax(filepath):
    """
    Pass 1: Fixes Tabs->Spaces and snaps irregular indentation.
    Pass 2: Adjusts def/class/if indentation based on body content.
    Visualizes changes with context.
    """
    try:
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()

        cleaned_lines = []
        for line in lines:
            line_expanded = line.replace('\t', '    ')
            stripped = line_expanded.lstrip()
            if not stripped:
                cleaned_lines.append(line_expanded)
                continue
            leading_spaces = len(line_expanded) - len(stripped)
            remainder = leading_spaces % 4
            if remainder == 0:
                cleaned_lines.append(line_expanded)
            else:
                if remainder <= 2:
                    new_spaces = leading_spaces - remainder
                else:
                    new_spaces = leading_spaces + (4 - remainder)
                cleaned_lines.append(' ' * new_spaces + stripped)

        final_lines = list(cleaned_lines)
        
        def get_indent(s):
            return len(s) - len(s.lstrip())

        for i in range(len(final_lines)):
            line = final_lines[i]
            stripped = line.lstrip()

            if stripped.startswith(('def ', 'class ', 'if ')):
                current_indent = get_indent(line)
                
                next_indents = []
                for j in range(i + 1, len(final_lines)):
                    if final_lines[j].strip():
                        next_indents.append(get_indent(final_lines[j]))
                    if len(next_indents) == 2:
                        break
                
                if len(next_indents) == 2:
                    b1, b2 = next_indents
                    if b1 == b2:
                        diff = b1 - current_indent
                        if diff < 4:
                            new_indent = max(0, current_indent - 4)
                            final_lines[i] = (' ' * new_indent) + stripped
                        elif diff == 8:
                            new_indent = current_indent + 4
                            final_lines[i] = (' ' * new_indent) + stripped

        changes_found = False
        print(f"\n{CYAN}Applying Syntax Fixes for: {filepath}{RESET}")
        
        for i, (orig, new) in enumerate(zip(lines, final_lines)):
            if orig != new:
                changes_found = True
                
                start_idx = max(0, i - 2)
                end_idx = min(len(lines), i + 3)
                
                print("-" * 40)

                for ctx in range(start_idx, i):
                    print(f"  {lines[ctx].rstrip()}")

                print(f"{RED}- {orig.rstrip()}{RESET}")
                print(f"{GREEN}+ {new.rstrip()}{RESET}")

                for ctx in range(i + 1, end_idx):
                    print(f"  {lines[ctx].rstrip()}")
        
        if not changes_found:
            print("  No changes needed.")
        else:
            print("-" * 40)

        with open(filepath, 'w', encoding='utf-8') as f:
            f.writelines(final_lines)
        return True
    except Exception as e:
        print(f"Fix failed: {e}")
        return False

def print_table(title, data, headers):
    if not data:
        print(f"\n{title}: No issues found.")
        return
    print(f"\n{title}")
    widths = [len(h) for h in headers]
    for row in data:
        for i, val in enumerate(row):
            widths[i] = max(widths[i], len(str(val)))
    widths = [w + 6 for w in widths]
    if len(widths) > 2:
        widths[2] -= 4
    if len(widths) > 4:
        widths[4] += 10
    h_str = " | ".join(f"{h:^{w}}" for h, w in zip(headers, widths))
    sep = "-" * len(h_str)
    print(sep)
    print(h_str)
    print(sep)
    for row in data:
        print(" | ".join(f"{str(val):<{w}}" for val, w in zip(row, widths)))
    print(sep + "\n")

def main():
    os.system('mode con: cols=225 lines=60')
    center_console()
    target_dir = os.getcwd()
    print(f"Target: {target_dir}")
    print("Exclusions loaded.")
    files = get_target_files(target_dir)
    table1_data = [] 
    files_with_junk = {}
    print("Scanning for comments and empty lines...")
    for f in files:
        items = analyze_comments(f)
        if items:
            files_with_junk[f] = items
            for item in items:
                cont = (item['content'][:30] + '..') if len(item['content']) > 30 else item['content']
                display_type = f"{item['action']}: {item['type']}"
                table1_data.append([os.path.basename(f), os.path.dirname(f), item['line'], display_type, cont])
    print_table("TABLE 1: Comments & Empty Lines", table1_data, ["File", "Path", "Line", "Type", "Content"])
    if table1_data:
        q = input(">>> Nuking junk from Table 1. Correct these? (Y/N): ").strip().upper()
        if q == 'Y':
            for f, items in files_with_junk.items():
                nuke_comments(f, items)
            print("Cleanup complete.")
        else:
            print("Skipping cleanup.")
    table2_data = []
    files_broken = {}
    print("\nScanning for Syntax/Indentation errors...")
    for f in files:
        err = check_syntax(f)
        if err:
            files_broken[f] = err
            table2_data.append([os.path.basename(f), f[-25:], err])
    print_table("TABLE 2: Syntax & Indentation", table2_data, ["File", "Path", "Error"])
    if table2_data:
        q = input(">>> Found Syntax/Indentation errors. Attempt to fix (Tabs->Spaces)? (Y/N): ").strip().upper()
        if q == 'Y':
            for f in files_broken:
                fix_syntax(f)
            print("Syntax fixes applied.")
        else:
            print("Skipping syntax fixes.")
    input("\nPress Enter to exit...")
    print("\nDone.")

if __name__ == "__main__":
    main()