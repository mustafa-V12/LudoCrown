#!/usr/bin/env python3
"""
Validates every SQL statement in src/LudoCrown.Server.Postgres/Sql.cs against a real PostgreSQL database
that has the migrations applied. Each statement is PREPAREd, which makes PostgreSQL fully parse and
type-check it against the schema (tables, columns, types, casts) without running it.

Usage:  python3 tools/verify-sql.py "postgresql://user:pass@localhost/ludocrown_verify"
"""
import re, subprocess, sys, pathlib

root = pathlib.Path(__file__).resolve().parent.parent
src = (root / "src/LudoCrown.Server.Postgres/Sql.cs").read_text()
db = sys.argv[1]

consts = {}
# Raw string literals: name = $"""...""" or """..."""
for m in re.finditer(r'const string (\w+) = \$?"""\s*\n(.*?)"""', src, re.S):
    consts[m.group(1)] = m.group(2)
# Regular string literals (possibly interpolated)
for m in re.finditer(r'const string (\w+) = \$?"((?:[^"\\]|\\.)*)";', src):
    consts[m.group(1)] = m.group(2)

def expand(text):
    # Resolve {ConstName} interpolation used in $"..." strings.
    return re.sub(r"\{(\w+)\}", lambda mm: consts.get(mm.group(1), mm.group(0)), text)

skip = {"ProfileColumns", "CreateMigrationsTable"}
# The migration runner creates its own bookkeeping table first; do the same here.
subprocess.run(["psql", db, "-q", "-X", "-c", consts["CreateMigrationsTable"]], check=True)
failures = 0
checked = 0
for name, text in consts.items():
    if name in skip:
        continue
    sql = expand(text).strip().rstrip(";")
    stmt = f"PREPARE q AS {sql};\nDEALLOCATE q;\n"
    r = subprocess.run(["psql", db, "-v", "ON_ERROR_STOP=1", "-q", "-X"], input=stmt, capture_output=True, text=True)
    checked += 1
    if r.returncode != 0:
        failures += 1
        print(f"FAIL {name}: {r.stderr.strip()}")
    else:
        print(f"  ok {name}")
print(f"\n{checked} statements checked, {failures} failed")
sys.exit(1 if failures else 0)
