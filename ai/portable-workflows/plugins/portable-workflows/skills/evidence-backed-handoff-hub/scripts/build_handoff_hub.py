#!/usr/bin/env python3
"""Build a self-contained offline HTML handoff hub from one JSON file."""

from __future__ import annotations

import argparse
import html
import json
import sys
from pathlib import Path
from typing import Any

from validate_handoff import validate_data, validate_html_text


def _escape(value: Any) -> str:
    return html.escape(str(value), quote=True)


def _details(items: list[str]) -> str:
    if not items:
        return ""
    rendered = "".join(f"<li>{_escape(item)}</li>" for item in items)
    return f'<ul class="details">{rendered}</ul>'


def _meta_line(label: str, value: str) -> str:
    if not value:
        return ""
    return f'<div class="meta"><span>{_escape(label)}</span>{_escape(value)}</div>'


def render_handoff(data: dict[str, Any]) -> str:
    """Render validated handoff data as escaped, dependency-free HTML."""
    evidence = data["evidence"]
    sections = data["sections"]
    questions = data["open_questions"]
    tables = data.get("tables", [])
    relationships = data.get("relationships", [])
    evidence_by_id = {item["id"]: item for item in evidence}

    evidence_cards = []
    for item in evidence:
        evidence_cards.append(
            "".join(
                [
                    '<article class="card evidence-card">',
                    '<div class="card-topline">',
                    f'<span class="eyebrow">{_escape(item["id"])}</span>',
                    f'<span class="badge">{_escape(item["status"])}</span>',
                    "</div>",
                    f'<h3>{_escape(item["title"])}</h3>',
                    f'<p>{_escape(item["summary"])}</p>',
                    _details(item.get("details", [])),
                    _meta_line("Source", item["source"]),
                    "</article>",
                ]
            )
        )

    section_cards = []
    for item in sections:
        evidence_labels = "".join(
            f'<li><span class="ref-id">{_escape(ref)}</span> '
            f'{_escape(evidence_by_id[ref]["title"])}</li>'
            for ref in item["evidence_ids"]
        )
        references = (
            f'<div class="references"><h4>Supporting evidence</h4><ul>{evidence_labels}</ul></div>'
            if evidence_labels
            else '<div class="references empty">No supporting evidence linked.</div>'
        )
        section_cards.append(
            "".join(
                [
                    '<article class="card section-card">',
                    f'<span class="eyebrow">{_escape(item["id"])}</span>',
                    f'<h3>{_escape(item["title"])}</h3>',
                    f'<p>{_escape(item["summary"])}</p>',
                    _details(item.get("details", [])),
                    references,
                    "</article>",
                ]
            )
        )

    question_cards = []
    for item in questions:
        question_cards.append(
            "".join(
                [
                    '<article class="card question-card">',
                    '<div class="card-topline">',
                    f'<span class="eyebrow">{_escape(item["id"])}</span>',
                    f'<span class="badge question-badge">{_escape(item["status"])}</span>',
                    "</div>",
                    f'<h3>{_escape(item["question"])}</h3>',
                    (
                        f'<p>{_escape(item.get("context", ""))}</p>'
                        if item.get("context")
                        else ""
                    ),
                    _meta_line("Owner", item.get("owner", "")),
                    "</article>",
                ]
            )
        )
    if not question_cards:
        question_cards.append(
            '<div class="empty-state">No open questions were recorded.</div>'
        )

    map_markup = ""
    if tables or relationships:
        table_cards = []
        for item in tables:
            field_rows = "".join(
                "<tr>"
                f'<th scope="row">{_escape(field["name"])}</th>'
                f'<td>{_escape(field["description"])}</td>'
                "</tr>"
                for field in item["fields"]
            )
            if not field_rows:
                field_rows = '<tr><td colspan="2" class="muted">No fields listed.</td></tr>'
            table_cards.append(
                "".join(
                    [
                        '<article class="card table-card">',
                        f'<span class="eyebrow">{_escape(item["id"])}</span>',
                        f'<h3>{_escape(item["name"])}</h3>',
                        f'<p>{_escape(item["description"])}</p>',
                        '<div class="table-wrap"><table><thead><tr><th>Field</th><th>Description</th></tr></thead>',
                        f"<tbody>{field_rows}</tbody></table></div>",
                        "</article>",
                    ]
                )
            )
        relationship_rows = "".join(
            "<tr>"
            f'<td><span class="ref-id">{_escape(item["from"])}</span></td>'
            '<td class="arrow" aria-label="connects to">&rarr;</td>'
            f'<td><span class="ref-id">{_escape(item["to"])}</span></td>'
            f'<td>{_escape(item["label"])}</td>'
            "</tr>"
            for item in relationships
        )
        relationship_block = ""
        if relationship_rows:
            relationship_block = (
                '<div class="relationship-panel"><h3>Relationships</h3>'
                '<div class="table-wrap"><table><thead><tr><th>From</th><th></th>'
                '<th>To</th><th>Meaning</th></tr></thead>'
                f"<tbody>{relationship_rows}</tbody></table></div></div>"
            )
        map_markup = (
            '<section class="content-section" id="data-map">'
            '<div class="section-heading"><span>04</span><div><h2>Table &amp; relationship map</h2>'
            '<p>Optional data model context and validated connection endpoints.</p></div></div>'
            f'<div class="card-grid table-grid">{"".join(table_cards)}</div>'
            f"{relationship_block}</section>"
        )
    question_number = "05" if map_markup else "04"

    subtitle = data.get("subtitle", "")
    last_updated = data.get("last_updated", "")
    count_cards = [
        ("Evidence", len(evidence)),
        ("Sections", len(sections)),
        ("Open questions", len(questions)),
    ]
    if tables:
        count_cards.append(("Tables", len(tables)))
    stats = "".join(
        f'<div class="stat"><strong>{count}</strong><span>{_escape(label)}</span></div>'
        for label, count in count_cards
    )

    return f"""<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{_escape(data["title"])}</title>
  <style>
    :root {{
      color-scheme: light;
      --ink: #17212b;
      --muted: #5c6975;
      --surface: #ffffff;
      --soft: #f1f5f6;
      --line: #dbe3e6;
      --accent: #176b68;
      --accent-soft: #dff2ef;
      --warm: #a84f2a;
      --warm-soft: #f9e9df;
      --shadow: 0 14px 40px rgba(29, 48, 58, 0.09);
    }}
    * {{ box-sizing: border-box; }}
    html {{ scroll-behavior: smooth; }}
    body {{
      margin: 0;
      color: var(--ink);
      background: linear-gradient(180deg, #e8f3f1 0, #f7f8f6 360px, #f7f8f6 100%);
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      line-height: 1.6;
    }}
    main {{ width: min(1160px, calc(100% - 40px)); margin: 0 auto; padding: 48px 0 80px; }}
    .hero {{
      position: relative;
      overflow: hidden;
      padding: clamp(32px, 6vw, 72px);
      color: #fff;
      background: #143e3c;
      border-radius: 28px;
      box-shadow: var(--shadow);
    }}
    .hero::after {{
      content: "";
      position: absolute;
      width: 300px;
      height: 300px;
      right: -100px;
      top: -120px;
      border-radius: 50%;
      background: rgba(120, 221, 207, 0.15);
    }}
    .kicker, .eyebrow {{
      display: inline-block;
      color: #78d6ca;
      font-size: 0.72rem;
      font-weight: 800;
      letter-spacing: 0.12em;
      text-transform: uppercase;
    }}
    h1, h2, h3, h4, p {{ margin-top: 0; }}
    h1 {{ max-width: 850px; margin: 12px 0 10px; font-size: clamp(2.25rem, 5vw, 4.6rem); line-height: 1.04; letter-spacing: -0.045em; }}
    .subtitle {{ max-width: 760px; margin-bottom: 12px; color: #d4e9e6; font-size: 1.15rem; }}
    .updated {{ color: #a9c7c3; font-size: 0.88rem; }}
    .stats {{ display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin-top: 34px; }}
    .stat {{ padding: 15px 16px; background: rgba(255,255,255,0.08); border: 1px solid rgba(255,255,255,0.13); border-radius: 14px; }}
    .stat strong, .stat span {{ display: block; }}
    .stat strong {{ font-size: 1.5rem; }}
    .stat span {{ color: #c7ddda; font-size: 0.78rem; text-transform: uppercase; letter-spacing: 0.07em; }}
    .content-section {{ padding-top: 62px; }}
    .section-heading {{ display: flex; gap: 16px; align-items: flex-start; margin-bottom: 24px; }}
    .section-heading > span {{ display: grid; place-items: center; min-width: 44px; height: 44px; color: var(--accent); background: var(--accent-soft); border-radius: 12px; font-weight: 900; }}
    .section-heading h2 {{ margin-bottom: 2px; font-size: clamp(1.6rem, 3vw, 2.35rem); letter-spacing: -0.03em; }}
    .section-heading p {{ margin-bottom: 0; color: var(--muted); }}
    .overview-panel {{ padding: clamp(24px, 4vw, 40px); background: var(--surface); border: 1px solid var(--line); border-left: 5px solid var(--accent); border-radius: 18px; box-shadow: var(--shadow); font-size: 1.08rem; white-space: pre-wrap; }}
    .card-grid {{ display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 18px; }}
    .card {{ padding: 24px; background: var(--surface); border: 1px solid var(--line); border-radius: 18px; box-shadow: 0 7px 24px rgba(29, 48, 58, 0.05); }}
    .card h3 {{ margin: 10px 0 8px; line-height: 1.28; letter-spacing: -0.015em; }}
    .card p {{ color: #33414c; white-space: pre-wrap; }}
    .card-topline {{ display: flex; justify-content: space-between; gap: 12px; align-items: center; }}
    .card .eyebrow {{ color: var(--accent); }}
    .badge {{ padding: 4px 10px; color: var(--accent); background: var(--accent-soft); border-radius: 999px; font-size: 0.76rem; font-weight: 800; }}
    .question-badge {{ color: var(--warm); background: var(--warm-soft); }}
    .details {{ margin: 14px 0 18px; padding-left: 1.2rem; color: #33414c; }}
    .details li + li {{ margin-top: 6px; }}
    .meta {{ display: grid; grid-template-columns: 80px 1fr; gap: 10px; padding-top: 14px; border-top: 1px solid var(--line); color: var(--muted); font-size: 0.88rem; overflow-wrap: anywhere; }}
    .meta span {{ color: var(--ink); font-weight: 800; }}
    .references {{ margin-top: 20px; padding: 15px 16px; background: var(--soft); border-radius: 12px; }}
    .references h4 {{ margin-bottom: 6px; font-size: 0.82rem; text-transform: uppercase; letter-spacing: 0.07em; }}
    .references ul {{ margin: 0; padding-left: 1.2rem; }}
    .references.empty {{ color: var(--muted); font-size: 0.88rem; }}
    .ref-id {{ display: inline-block; padding: 2px 7px; background: #e3e9eb; border-radius: 6px; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 0.82em; overflow-wrap: anywhere; }}
    .empty-state {{ padding: 28px; color: var(--muted); background: var(--surface); border: 1px dashed #b8c5ca; border-radius: 16px; text-align: center; }}
    .table-grid {{ align-items: start; }}
    .table-wrap {{ overflow-x: auto; }}
    table {{ width: 100%; border-collapse: collapse; font-size: 0.9rem; }}
    th, td {{ padding: 10px 12px; border-bottom: 1px solid var(--line); text-align: left; vertical-align: top; }}
    thead th {{ color: var(--muted); background: var(--soft); font-size: 0.72rem; text-transform: uppercase; letter-spacing: 0.07em; }}
    tbody th {{ width: 34%; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; overflow-wrap: anywhere; }}
    .relationship-panel {{ margin-top: 18px; padding: 24px; background: #21353c; color: #fff; border-radius: 18px; box-shadow: var(--shadow); }}
    .relationship-panel h3 {{ margin-bottom: 12px; }}
    .relationship-panel table {{ color: #eaf2f3; }}
    .relationship-panel thead th {{ color: #b8cbd0; background: rgba(255,255,255,0.06); }}
    .relationship-panel th, .relationship-panel td {{ border-color: rgba(255,255,255,0.12); }}
    .relationship-panel .ref-id {{ color: #fff; background: rgba(255,255,255,0.11); }}
    .arrow {{ width: 42px; color: #78d6ca; text-align: center; font-size: 1.2rem; }}
    .muted {{ color: var(--muted); }}
    footer {{ margin-top: 60px; padding-top: 22px; color: var(--muted); border-top: 1px solid var(--line); font-size: 0.82rem; }}
    @media (max-width: 760px) {{
      main {{ width: min(100% - 24px, 1160px); padding-top: 12px; }}
      .hero {{ border-radius: 18px; }}
      .stats, .card-grid {{ grid-template-columns: 1fr; }}
      .stats {{ grid-template-columns: repeat(2, minmax(0, 1fr)); }}
      .content-section {{ padding-top: 44px; }}
      .section-heading {{ gap: 10px; }}
      .meta {{ grid-template-columns: 1fr; gap: 2px; }}
    }}
    @media print {{
      body {{ background: #fff; }}
      main {{ width: 100%; padding: 0; }}
      .hero, .card, .overview-panel, .relationship-panel {{ box-shadow: none; break-inside: avoid; }}
      .content-section {{ padding-top: 32px; }}
    }}
  </style>
</head>
<body>
  <main>
    <header class="hero">
      <span class="kicker">Evidence-backed handoff</span>
      <h1>{_escape(data["title"])}</h1>
      {f'<p class="subtitle">{_escape(subtitle)}</p>' if subtitle else ''}
      {f'<div class="updated">Last updated: {_escape(last_updated)}</div>' if last_updated else ''}
      <div class="stats">{stats}</div>
    </header>

    <section class="content-section" id="overview">
      <div class="section-heading"><span>01</span><div><h2>Overview</h2><p>Orientation for a reader entering without prior context.</p></div></div>
      <div class="overview-panel">{_escape(data["overview"])}</div>
    </section>

    <section class="content-section" id="evidence">
      <div class="section-heading"><span>02</span><div><h2>Evidence</h2><p>Source-labeled facts with explicit confidence or review status.</p></div></div>
      <div class="card-grid">{''.join(evidence_cards)}</div>
    </section>

    <section class="content-section" id="system-sections">
      <div class="section-heading"><span>03</span><div><h2>System sections</h2><p>Explanations connected to the evidence that supports them.</p></div></div>
      <div class="card-grid">{''.join(section_cards)}</div>
    </section>

    {map_markup}

    <section class="content-section" id="open-questions">
      <div class="section-heading"><span>{question_number}</span><div><h2>Open questions</h2><p>Unresolved decisions, missing facts, and ownership cues.</p></div></div>
      <div class="card-grid">{''.join(question_cards)}</div>
    </section>

    <footer>Built from the accompanying structured JSON. Rebuild from that source instead of editing this HTML manually.</footer>
  </main>
</body>
</html>
"""


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Build a self-contained offline handoff hub from JSON."
    )
    parser.add_argument("input", type=Path, help="UTF-8 handoff JSON file")
    parser.add_argument("output", type=Path, help="Destination HTML file")
    args = parser.parse_args(argv)

    try:
        with args.input.open("r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        print(f"ERROR input: could not read valid UTF-8 JSON: {exc}", file=sys.stderr)
        return 1

    errors = validate_data(data)
    if errors:
        for error in errors:
            print(f"ERROR {error}", file=sys.stderr)
        print(f"Build stopped with {len(errors)} validation error(s).", file=sys.stderr)
        return 1

    rendered = render_handoff(data)
    html_errors = validate_html_text(rendered)
    if html_errors:
        for error in html_errors:
            print(f"ERROR {error}", file=sys.stderr)
        print("Build stopped because the rendered HTML is not offline-safe.", file=sys.stderr)
        return 1

    try:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8", newline="\n")
    except (OSError, UnicodeError) as exc:
        print(f"ERROR output: could not write HTML: {exc}", file=sys.stderr)
        return 1

    question_count = len(data["open_questions"])
    question_label = "open question" if question_count == 1 else "open questions"
    section_count = len(data["sections"])
    section_label = "section" if section_count == 1 else "sections"
    table_count = len(data.get("tables", []))
    table_label = "table" if table_count == 1 else "tables"
    print(
        "Built offline handoff hub: "
        f"{len(data['evidence'])} evidence, {section_count} {section_label}, "
        f"{question_count} {question_label}, "
        f"{table_count} {table_label} -> {args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
