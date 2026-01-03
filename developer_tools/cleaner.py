import sys
import os

def clean_python_file(filepath):
    if not os.path.exists(filepath):
        return
    with open(filepath, 'r', encoding='utf-8') as f:
        lines = f.readlines()
    no_comments = []
    in_multiline_string = False
    for line in lines:
        line = line.rstrip()
        stripped = line.strip()
        if '"""' in stripped and stripped.count('"""') % 2 != 0:
            in_multiline_string = not in_multiline_string
            if stripped == '"""':
                continue
        if in_multiline_string or '"""' in stripped:
            no_comments.append(line)
            continue
        if stripped.startswith('#'):
            continue
        line = line.split('#', 1)[0].rstrip()
        if line:
            no_comments.append(line)
    final_code = []
    for i, line in enumerate(no_comments):
        is_class = line.lstrip().startswith('class ')
        is_def = line.lstrip().startswith('def ')
        if (is_class or is_def) and i > 0:
            prev_line = no_comments[i-1]
            if prev_line.strip() != '' and not prev_line.strip().startswith('@'):
                final_code.append('')
        final_code.append(line)
    with open(filepath, 'w', encoding='utf-8') as f:
        if final_code:
            f.write('\n'.join(final_code) + '\n')
if __name__ == "__main__":
    for filepath in sys.argv[1:]:
        clean_python_file(filepath)
