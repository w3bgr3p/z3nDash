#!/usr/bin/env python3
"""
Инвентарь extension-методов: ZpRuntime против ядра z3n7.

z3n7 — эталон поведения: скрипты пишутся под него, наша задача его воспроизводить.
Поэтому при пересечении выигрывает z3n7, а наша версия удаляется. Скрипт показывает,
где пересечение есть и где расходятся возвращаемые типы — то есть где поведение
разъедется не на сборке, а на исполнении.

Запуск из корня репозитория:
    python ZpRuntime/tools/ext_inventory.py
    python ZpRuntime/tools/ext_inventory.py --z3n7 D:/path/to/z3n7/z3n7

Репозиторий z3n7 только читается — ничего в нём не меняется.
"""

import argparse
import os
import re
import sys

# public static <ret> <Name>(this <Receiver> ...
SIGNATURE = re.compile(
    r'public\s+static\s+([\w<>\[\],\s\.\?]+?)\s+(\w+)\s*\(\s*this\s+([\w\.]+)\s'
)

OURS = [
    'ZpRuntime/Zenno/ZennoStub.cs',
    'ZpRuntime/Browser/Extensions.cs',
    'ZpRuntime/Browser/CanvasExtensions.cs',
]

# Дословные копии из эталона. Расходиться с z3n7 не могут по построению, поэтому
# считаются отдельно — как сделанная часть переноса, а не как наша реализация.
PORTED_DIR = 'ZpRuntime/Z3n7'

DEFAULT_Z3N7 = 'W:/code_hard/.net/z3n7/z3n7'


def cs_files(root):
    found = []
    for path, _, names in os.walk(root):
        if any(part in path for part in ('\\obj', '\\bin', '/obj', '/bin')):
            continue
        found += [os.path.join(path, n) for n in names if n.endswith('.cs')]
    return found


def scan(paths):
    """{(receiver, method): (return_type, file)} — первое вхождение."""
    table = {}
    for path in paths:
        try:
            with open(path, encoding='utf-8', errors='replace') as fh:
                text = fh.read()
        except OSError as err:
            print(f'  пропущен {path}: {err}', file=sys.stderr)
            continue
        for m in SIGNATURE.finditer(text):
            ret = ' '.join(m.group(1).split())
            key = (m.group(3).split('.')[-1], m.group(2))
            table.setdefault(key, (ret, os.path.basename(path)))
    return table


def normalize(type_name):
    """System.Collections.Generic.Dictionary<..> и Dictionary<..> — одно и то же."""
    return re.sub(r'[\w\.]*\.(\w+<)', r'\1', type_name).replace(' ', '')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--z3n7', default=DEFAULT_Z3N7, help='корень проекта-ядра z3n7')
    args = ap.parse_args()

    if not os.path.isdir(args.z3n7):
        print(f'не найден каталог z3n7: {args.z3n7}', file=sys.stderr)
        return 2

    ours = scan(OURS)
    theirs = scan(cs_files(args.z3n7))
    ported = scan(cs_files(PORTED_DIR)) if os.path.isdir(PORTED_DIR) else {}

    shared = sorted(set(ours) & set(theirs))
    only_ours = sorted(set(ours) - set(theirs))
    # перенесённое из остатка вычитаем: оно уже на месте
    only_theirs = sorted(set(theirs) - set(ours) - set(ported))

    done = len(set(ported) & set(theirs))
    print(f'наши: {len(ours)}   z3n7: {len(theirs)}   пересечение: {len(shared)}'
          f'   перенесено: {done}')

    print(f'\n=== ПЕРЕСЕЧЕНИЕ ({len(shared)}) — при копировании конфликтует ===')
    divergent = 0
    for key in shared:
        our_ret, our_file = ours[key]
        their_ret, _ = theirs[key]
        same = normalize(our_ret) == normalize(their_ret)
        if not same:
            divergent += 1
        mark = '' if same else '   <-- РАСХОЖДЕНИЕ ТИПА'
        print(f'  {key[0]}.{key[1]:<24} наш:{our_ret:<26} z3n7:{their_ret:<26}{mark}')
        if not same:
            print(f'      наша версия в {our_file}')

    print(f'\n=== только у нас ({len(only_ours)}) ===')
    for recv, name in only_ours:
        print(f'  {recv}.{name}')

    print(f'\n=== только в z3n7 ({len(only_theirs)}) — переносится как есть ===')
    for recv, name in only_theirs:
        print(f'  {recv}.{name}')

    print(f'\nитого к разрешению: {len(shared)}, из них с расхождением типа: {divergent}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
