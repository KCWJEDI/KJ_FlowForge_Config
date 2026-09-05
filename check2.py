import os

file_path = r'W:\WorkSpace\KJ_FlowForge_Config\src\Program.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Check for the string
search = 'pull --rebase origin main'
count = content.count(search)
print(f'Found {count} occurrences of: {search}')

# Show all occurrences
idx = 0
while True:
    idx = content.find(search, idx)
    if idx < 0:
        break
    start = max(0, idx - 30)
    end = min(len(content), idx + 50)
    print(f'Context at {idx}: ...{content[start:end]}...')
    idx += len(search)
