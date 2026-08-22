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
import difflib
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
#
# Критерий отбора здесь не «компилируется ли», а «есть ли под нами то, к чему
# оно обращается». Инфраструктура самого ZennoPoster под нами не значит ничего:
# компилируется она прекрасно, а работать ей не с чем.
#
# ProjectExtencions.cs: Help — просмотр методов ZennoLab через рефлексию,
#   инструмент разработки. В скомпилированных рабочих скриптах его нет.
# Extractor.cs, ZpToCsx.cs: конвертация ZP-шаблона в csx. Мы играем XML
#   напрямую через XmlPlayer — конвертировать не во что.
# ExternalCode.cs: RunZp — запуск .zp файла процессом ZennoPoster. Такого
#   процесса под нами нет, и файл без конвертации всё равно не запустится.
# LogDisabler.cs: гасит папку Logs рядом с ZennoPoster.exe. У нашего процесса
#   её нет.
# TaskManager.cs: управление очередью задач ZP — ExportInputSettings, TasksList,
#   StartTask, SetMaxThreads. Пятнадцать обращений к ZennoPoster.*, и все они у
#   нас помечены «отказ»: очереди ZP нет, планировщик свой.
#
# Ниже — куст жизненного цикла инстанса ZP. Формулировка Master: «это часть
# именно для ZP, она не имеет смысла и к нашей подложке отношения не имеет».
# Проверено по тому, чем каждый файл ходит наружу:
# ZpServer.cs: Start/StopZpServer — сервер управления ZennoPoster, 10 обращений
#   к ZennoPoster.*.
# Init.cs: первым делом зовёт StartZpServer, то есть тянет тот же сервер.
# ProcessManager.cs: гасит процессы zbe1 (движок ZennoBrowser) и читает WMI. Мы
#   поднимаем Patchright, таких процессов нет.
# ProcAcc.cs: ищет процессы ZP по имени и разбирает их CommandLine через WMI,
#   чтобы привязать аккаунт к процессу.
# ChromeExt.cs: Emulator.SendKey по ActiveTab.Handle — системная эмуляция клавиш
#   в окно браузера ZP. HWND у нас нет и не будет, Tab.Handle суррогат.
# ProfileSync.cs: раскладка профиля ZP по колонкам БД плюс
#   instance.WebGLPreferences.Load — формат профиля ZP.
# Disposer.cs: собран из InstanceManager и Reporter, то есть весь тот же цикл.
# Reporter.cs: ZennoPoster.ImageProcessing* по instance.Port — обработка
#   скриншотов сервисом ZP, у нас уже «отказ».
SKIP_FILES = {
    'ProjectExtencions.cs',
    'Extractor.cs',
    'ZpToCsx.cs',
    'ExternalCode.cs',
    'LogDisabler.cs',
    'TaskManager.cs',
    'ZpServer.cs',
    'Init.cs',
    'ProcessManager.cs',
    'ProcAcc.cs',
    'ChromeExt.cs',
    'ProfileSync.cs',
    'Disposer.cs',
    'Reporter.cs',
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


def read_lines(path):
    """Строки файла без CR и BOM — сравнение не должно спотыкаться о перевод строки."""
    try:
        with open(path, encoding='utf-8-sig', errors='replace') as fh:
            return [l.rstrip() for l in fh.readlines()]
    except OSError:
        return []


def drift(ported_dir, z3n7_root):
    """
    Сверка дословных копий с эталоном.

    Копии в ZpRuntime/Z3n7 начинаются с нашей шапки, поэтому сравнение идёт со
    сдвигом: ищем строку, с которой начинается эталон, и от неё сверяем остаток.
    Совпало — копия дословная; не совпало — либо помеченное отступление, либо
    эталон ушёл вперёд, и это надо разбирать глазами.

    Ради этой сверки всё и затевалось: за три недели без неё FastDb отстал от
    эталона по существу, а ListExtentions и Time оказались не совсем дословными
    с самого начала. Глазом такое не видно.
    """
    ref = {os.path.basename(f): f for f in cs_files(z3n7_root)}
    exact, deviated, orphan = [], [], []

    for f in sorted(cs_files(ported_dir)):
        name = os.path.basename(f)
        if name not in ref:
            orphan.append(name)
            continue

        ours, theirs = read_lines(f), read_lines(ref[name])
        if any(ours[n:] == theirs for n in range(0, 40)):
            exact.append(name)
            continue

        first = theirs[0].strip() if theirs else ''
        start = next((n for n in range(0, 40)
                      if n < len(ours) and ours[n].strip() == first), 0)
        d = [l for l in difflib.unified_diff(theirs, ours[start:], lineterm='', n=0)
             if l[:1] in '+-' and not l.startswith(('+++', '---'))]
        deviated.append((name,
                         sum(1 for l in d if l[0] == '+'),
                         sum(1 for l in d if l[0] == '-'),
                         os.path.relpath(ref[name], z3n7_root).replace(os.sep, '/')))

    return exact, deviated, orphan



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

    # Файлы. Секции выше видят только extension-методы и объявления типов, а
    # обычный класс без того и другого в них не попадает вовсе. Так был пропущен
    # Api/AnyMessage.cs: ветка шаблона зовёт его по имени, а инвентарь молчал,
    # и я успел объявить перенос законченным. Здесь остаток считается по файлам.
    ported_files = {os.path.basename(f) for f in cs_files(PORTED_DIR)}
    their_files  = {os.path.basename(f): f for f in cs_files(args.z3n7)}
    missing = sorted(n for n in their_files if n not in ported_files)

    print(f'\n=== файлы ядра z3n7 без пары ({len(missing)} из {len(their_files)}) ===')
    for name in missing:
        path = their_files[name]
        try:
            with open(path, encoding='utf-8', errors='replace') as fh:
                lines = sum(1 for _ in fh)
        except OSError:
            lines = 0
        rel = os.path.relpath(path, args.z3n7).replace(os.sep, '/')
        print(f'  {lines:>5} строк  {rel}')

    # Дрейф копий. Считается последним: это единственная секция, где «всё
    # хорошо» означает буквально пустоту, а любая строка — повод открыть diff.
    exact, deviated, orphan = drift(PORTED_DIR, args.z3n7)

    print()
    print(f'=== дословность копий: совпадают {len(exact)}, '
          f'с отличиями {len(deviated)}, без пары {len(orphan)} ===')
    for name, adds, dels, rel in deviated:
        print(f'  {name:<26} +{adds} -{dels}   <- {rel}')
    if orphan:
        print('  без пары в эталоне: ' + ', '.join(orphan))
    if deviated:
        print('  каждая строка выше — либо помеченное отступление, либо ушедший')
        print('  вперёд эталон; отличать надо diff-ом, счётчик этого не знает')

    print()
    print(f'итого к разрешению: {len(shared)}, из них с расхождением типа: {divergent}'
          f'; типов под вопросом: {len(candidates)}; файлов без пары: {len(missing)}'
          f'; копий с отличиями: {len(deviated)}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
