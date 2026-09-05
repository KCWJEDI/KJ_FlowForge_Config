import os

file_path = r'W:\WorkSpace\KJ_FlowForge_Config\src\Program.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Check for the string
search = 'pull --rebase origin main'
count = content.count(search)
print(f'Found {count} occurrences of: {search}')

# Show context around first occurrence
idx = content.find(search)
if idx >= 0:
    start = max(0, idx - 50)
    end = min(len(content), idx + 100)
    print(f'Context: ...{content[start:end]}...')
