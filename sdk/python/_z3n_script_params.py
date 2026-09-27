"""Reads the command-line parameters a Python script declares, without running it.

z3nDash calls this as `python -I _z3n_script_params.py <script>` and reads one
JSON object from stdout. The script is only parsed with `ast`, never imported or
executed, so a script with side effects at the top level is safe to inspect.

Recognised: argparse `add_argument` calls (with subcommands from `add_parser`)
and click `@option` / `@argument` decorators. Anything built at run time -
names from variables, loops, `sys.argv` read by hand - is reported, not guessed.
"""
import ast
import json
import sys

ARG_GROUPS = {"add_argument_group", "add_mutually_exclusive_group"}
CLICK_DECORATORS = {"option", "argument"}


def _source(node):
    unparse = getattr(ast, "unparse", None)
    if unparse is not None:
        return unparse(node)
    return ast.dump(node)


def _literal(node):
    """(True, value) for a JSON-friendly literal, otherwise (False, source text)."""
    try:
        value = ast.literal_eval(node)
    except Exception:
        return False, _source(node)
    if isinstance(value, (tuple, set, frozenset)):
        value = list(value)
    try:
        json.dumps(value)
    except (TypeError, ValueError):
        return False, _source(node)
    return True, value


def _call_name(func):
    if isinstance(func, ast.Attribute):
        return func.attr
    if isinstance(func, ast.Name):
        return func.id
    return ""


def _receiver(func):
    if isinstance(func, ast.Attribute):
        return _source(func.value)
    return ""


def _describe(call, kind):
    """One parameter from the call's positional names and keyword arguments."""
    param = {"kind": kind, "line": call.lineno, "names": []}
    exprs = {}
    for arg in call.args:
        ok, value = _literal(arg)
        if ok and isinstance(value, str):
            param["names"].append(value)
        else:
            exprs.setdefault("names", []).append(value)
    for kw in call.keywords:
        if kw.arg is None:
            exprs["**"] = _source(kw.value)
            continue
        if kw.arg == "type":
            param["type"] = _source(kw.value)
            continue
        ok, value = _literal(kw.value)
        if ok:
            param[kw.arg] = value
        else:
            exprs[kw.arg] = value
    if exprs:
        param["exprs"] = exprs
    names = param["names"]
    param["positional"] = bool(names) and not any(n.startswith("-") for n in names)
    return param


def _subcommand_map(tree):
    """Variable name -> subcommand, from `x = sub.add_parser("run")` and its groups."""
    owner = {}
    assigns = [n for n in ast.walk(tree)
               if isinstance(n, ast.Assign) and isinstance(n.value, ast.Call)]
    assigns.sort(key=lambda n: (n.lineno, n.col_offset))
    for node in assigns:
        call = node.value
        name = _call_name(call.func)
        targets = [t.id for t in node.targets if isinstance(t, ast.Name)]
        if name == "add_parser" and call.args:
            ok, value = _literal(call.args[0])
            sub = value if ok and isinstance(value, str) else None
        elif name in ARG_GROUPS:
            sub = owner.get(_receiver(call.func))
        else:
            continue
        for target in targets:
            owner[target] = sub
    return owner


def _uses_sys_argv(tree):
    for node in ast.walk(tree):
        if (isinstance(node, ast.Attribute) and node.attr == "argv"
                and isinstance(node.value, ast.Name) and node.value.id == "sys"):
            return True
        if isinstance(node, ast.ImportFrom) and node.module == "sys":
            if any(alias.name == "argv" for alias in node.names):
                return True
    return False


def inspect(path):
    with open(path, "rb") as handle:
        source = handle.read()
    tree = ast.parse(source, filename=path)

    owner = _subcommand_map(tree)
    params, subcommands, descriptions = [], [], []

    for node in ast.walk(tree):
        if isinstance(node, ast.Call):
            name = _call_name(node.func)
            if name == "add_argument":
                param = _describe(node, "argparse")
                param["parser"] = _receiver(node.func)
                sub = owner.get(param["parser"])
                if sub:
                    param["subcommand"] = sub
                params.append(param)
            elif name == "add_parser" and node.args:
                ok, value = _literal(node.args[0])
                if ok and isinstance(value, str):
                    subcommands.append(value)
            elif name == "ArgumentParser":
                for kw in node.keywords:
                    if kw.arg == "description":
                        ok, value = _literal(kw.value)
                        if ok and isinstance(value, str):
                            descriptions.append(value)
        elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            for deco in node.decorator_list:
                if isinstance(deco, ast.Call) and _call_name(deco.func) in CLICK_DECORATORS:
                    param = _describe(deco, "click-" + _call_name(deco.func))
                    param["function"] = node.name
                    params.append(param)

    params.sort(key=lambda p: p["line"])
    return {
        "ok": True,
        "description": descriptions[0] if descriptions else None,
        "params": params,
        "subcommands": subcommands,
        "uses_sys_argv": _uses_sys_argv(tree),
        "unresolved": sum(1 for p in params if not p["names"]),
    }


def main():
    if len(sys.argv) != 2:
        result = {"ok": False, "step": "args", "error": "usage: _z3n_script_params.py <script.py>"}
    else:
        try:
            result = inspect(sys.argv[1])
        except SyntaxError as error:
            result = {"ok": False, "step": "parse",
                      "error": f"SyntaxError: {error.msg} (line {error.lineno})"}
        except Exception as error:
            result = {"ok": False, "step": "read", "error": f"{type(error).__name__}: {error}"}
    sys.stdout.buffer.write(json.dumps(result, ensure_ascii=False).encode("utf-8"))


if __name__ == "__main__":
    main()
