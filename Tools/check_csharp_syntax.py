#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""C# 语法自检（无需 Unity / dotnet）。

本机没有 Unity 编辑器，交付前至少保证新增 C# 文件在**语法**上没有错误：
用 tree-sitter 的 C# 语法树解析每个 .cs，报告 ERROR / MISSING 节点。

用法：
    python Tools/check_csharp_syntax.py                       # 检查 Assets/Scripts 全部 .cs
    python Tools/check_csharp_syntax.py Assets/Scripts/Streaming
    python Tools/check_csharp_syntax.py --quiet a.cs b.cs

退出码：0 = 全部通过；1 = 发现语法错误；2 = 依赖缺失。
"""
import os
import sys

try:
    from tree_sitter import Language, Parser
    import tree_sitter_c_sharp as _tscs
except Exception as exc:  # pragma: no cover
    sys.stderr.write("缺少依赖：pip install tree_sitter tree_sitter_c_sharp\n%s\n" % exc)
    sys.exit(2)

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_TARGETS = [os.path.join(ROOT, "Assets", "Scripts")]


def iter_cs(paths):
    for path in paths:
        if os.path.isfile(path) and path.endswith(".cs"):
            yield path
        elif os.path.isdir(path):
            for dirpath, _dirnames, filenames in os.walk(path):
                for name in sorted(filenames):
                    if name.endswith(".cs"):
                        yield os.path.join(dirpath, name)


def walk_errors(node, out, limit=20):
    if node.type == "ERROR" or getattr(node, "is_missing", False):
        out.append(node)
        if len(out) >= limit:
            return
    for child in node.children:
        walk_errors(child, out, limit)
        if len(out) >= limit:
            return


def main(argv):
    quiet = "--quiet" in argv
    paths = [a for a in argv if not a.startswith("--")] or DEFAULT_TARGETS
    language = Language(_tscs.language())
    parser = Parser(language)

    files = list(iter_cs(paths))
    if not files:
        sys.stderr.write("没有找到 .cs 文件：%s\n" % paths)
        return 2

    failed = 0
    for path in files:
        with open(path, "rb") as fh:
            source = fh.read()
        tree = parser.parse(source)
        if not tree.root_node.has_error:
            continue
        errors = []
        walk_errors(tree.root_node, errors)
        failed += 1
        rel = os.path.relpath(path, ROOT)
        print("[语法错误] %s" % rel)
        for node in errors[:8]:
            row, col = node.start_point
            end_row, end_col = node.end_point
            snippet = source[node.start_byte:min(node.end_byte, node.start_byte + 120)]
            snippet = snippet.decode("utf-8", "replace").replace("\n", "\\n")
            print("  %s:%d:%d  type=%s  %r" % (rel, row + 1, col + 1, node.type, snippet))
        if len(errors) >= 8:
            print("  … 其余错误省略")

    if not quiet:
        total = len(files)
        print("检查 %d 个 .cs 文件：%s" % (total, "全部通过" if failed == 0 else "%d 个文件有语法错误" % failed))
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
