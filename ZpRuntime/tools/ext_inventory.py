#!/usr/bin/env python3
"""
Инвентарь extension-методов: весь DevDeck против ядра z3n7.

z3n7 — эталон поведения: скрипты пишутся под него, наша задача его воспроизводить.
Поэтому при пересечении выигрывает z3n7, а наша версия удаляется. Скрипт показывает,
где пересечение есть и где расходятся возвращаемые типы — то есть где поведение
разъедется не на сборке, а на исполнении.

Сканируется весь репозиторий, а не только ZpRuntime. Прежняя версия смотрела три
файла из ZpRuntime и из-за этого пропустила StringToHex/HexToString в
Web3/StringExtentions.cs — дословные копии эталонных, простоявшие дублем весь
перенос. Дубль может лежать где угодно: язык разводит методы по namespace, а не
по каталогу.

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

# Типы: class/interface/enum/struct. Нужны потому, что дубли бывают не только
# среди extension-методов — за один заход мимо прежней версии скрипта прошли
# Time, NetHttpAsync и FunctionStorage.
TYPE_DECL = re.compile(
    r'public\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+)*'
    r'(class|interface|enum|struct)\s+(\w+)'
)

# Наша сторона — весь репозиторий, кроме перенесённого и артефактов сборки.
OURS_ROOT = '.'

# Дословные копии из эталона. Расходиться с z3n7 не могут по построению, поэтому
# считаются отдельно — как сделанная часть переноса, а не как наша реализация.
PORTED_DIR = 'ZpRuntime/Z3n7'

# Каталоги, которые в обход не попадают. obj/bin — вывод сборки, там лежат копии
# исходников из SDK-генераторов и они дают фантомные дубли. installer_output и
# publish-new — упакованные сборки. PORTED_DIR исключён отдельно: он считается
# не «нашим», а сделанной частью переноса.
SKIP_DIRS = {
    'obj', 'bin', '.git', '.idea', '.vs', 'node_modules',
    'installer_output', 'publish-new',
}

DEFAULT_Z3N7 = 'W:/code_hard/.net/z3n7/z3n7'

# Файлы эталона, которые переносить не нужно, — иначе они вечно висят в остатке.
# MethodExtensions/ProjectExtencions.cs: Help — просмотр методов ZennoLab через
# рефлексию, инструмент разработки. В скомпилированных рабочих скриптах его нет.
SKIP_FILES = {
    'ProjectExtencions.cs',
}


def cs_files(root, exclude=()):
    """Все .cs под root, кроме SKIP_DIRS, SKIP_FILES и путей из exclude."""
    excluded = [os.path.normpath(p) for p in exclude]
    found = []
    for path, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        here = os.path.normpath(path)
        if any(here == e or here.startswith(e + os.sep) for e in excluded):
            dirs[:] = []
            continue
        found += [os.path.join(path, n) for n in names
                  if n.endswith('.cs') and n not in SKIP_FILES]
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


def scan_types(paths):
    """{имя типа: файл} — первое вхождение."""
    table = {}
    for path in paths:
        try:
            with open(path, encoding='utf-8', errors='replace') as fh:
                text = fh.read()
        except OSError as err:
            print(f'  пропущен {path}: {err}', file=sys.stderr)
            continue
        for m in TYPE_DECL.finditer(text):
            table.setdefault(m.group(2), os.path.basename(path))
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

    if not os.path.isdir(PORTED_DIR):
        print(f'запускать из корня репозитория: не найден {PORTED_DIR}', file=sys.stderr)
        return 2

    ours = scan(cs_files(OURS_ROOT, exclude=[PORTED_DIR]))
    theirs = scan(cs_files(args.z3n7))
    ported = scan(cs_files(PORTED_DIR))

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
    for key in only_ours:
        print(f'  {key[0] + "." + key[1]:<40} {ours[key][1]}')

    print(f'\n=== только в z3n7 ({len(only_theirs)}) — переносится как есть ===')
    for recv, name in only_theirs:
        print(f'  {recv}.{name}')

    # Типы. Одноимённый класс в другом namespace сам по себе не ошибка — так у нас
    # сосуществуют Logger, SAFU, Db. Но если тип есть с обеих сторон и при этом не
    # перенесён, это кандидат: скорее всего наша самостоятельная реализация того же,
    # что уже написано в эталоне. Так в своё время нашлись Time и NetHttpAsync.
    our_types = scan_types(cs_files(OURS_ROOT, exclude=[PORTED_DIR]))
    their_types = scan_types(cs_files(args.z3n7))
    ported_types = scan_types(cs_files(PORTED_DIR))

    candidates = sorted(set(our_types) & set(their_types) - set(ported_types))
    coexist = sorted(set(our_types) & set(their_types) & set(ported_types))

    print(f'\n=== одноимённые типы, не перенесённые ({len(candidates)}) — проверить ===')
    for name in candidates:
        print(f'  {name:<24} наш:{our_types[name]:<26} z3n7:{their_types[name]}')

    print(f'\n=== одноимённые типы, уже сосуществуют ({len(coexist)}) ===')
    print('  ' + ', '.join(coexist) if coexist else '  —')

    print(f'\nитого к разрешению: {len(shared)}, из них с расхождением типа: {divergent}'
          f'; типов под вопросом: {len(candidates)}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
