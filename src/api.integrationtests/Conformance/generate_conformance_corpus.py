#!/usr/bin/env python3
"""Generate and verify the synthetic DMARC application bake-off corpus.

The corpus is deliberately independent of customer and upstream fixture data. It
uses only reserved ``.example`` names and documentation address ranges. Archive
and MIME metadata are fixed so output is byte-for-byte reproducible.
"""

from __future__ import annotations

import argparse
import codecs
import gzip
import hashlib
import io
import ipaddress
import json
import re
import sys
import zipfile
from collections.abc import Mapping, Sequence
from pathlib import Path, PurePosixPath
from typing import Any
from xml.sax.saxutils import escape

REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT_ROOT = Path(__file__).resolve().parents[1] / "Fixtures" / "Conformance"
CORPUS_SCHEMA_ID = "dmarc-analyzer.conformance-corpus/v1"
EXPECTED_SCHEMA_ID = "dmarc-analyzer.conformance-expected-state/v1"
RECOVERABLE_SOURCE = {
    "repository": "MTG-Thomas/bifrost-infra",
    "commit": "258d606e1f8ce859fff5422d550d469c3b666111",
    "generator_path": "scripts/generate-dmarc-bakeoff-corpus.py",
}
FIXED_RANGE_BEGIN = 1_785_888_000
FIXED_RANGE_END = 1_785_974_399
ZIP_TIMESTAMP = (1980, 1, 1, 0, 0, 0)
DOCUMENTATION_NETWORKS = (
    ipaddress.ip_network("192.0.2.0/24"),
    ipaddress.ip_network("198.51.100.0/24"),
    ipaddress.ip_network("203.0.113.0/24"),
    ipaddress.ip_network("2001:db8::/32"),
)
CORRUPT_GZIP_PAYLOAD = b"\x1f\x8b\x08\x00\x00\x00\x00\x00\x02\xffsynthetic-corruption"
DOMAIN_TOKEN = re.compile(
    r"(?i)(?<![a-z0-9_-])(?:[a-z0-9](?:[a-z0-9_-]{0,61}[a-z0-9])?\.)+[a-z]{2,}(?![a-z0-9_-])"
)
IP_TOKEN = re.compile(
    r"(?<![0-9a-f:.])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.]*(?![0-9a-f:.])"
    r"|(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])",
    re.IGNORECASE,
)


MANIFEST_SCHEMA: dict[str, Any] = {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "$id": "https://dmarc-analyzer.example/schemas/conformance-corpus-v1.json",
    "title": "DmarcAnalyzer synthetic conformance corpus",
    "type": "object",
    "additionalProperties": False,
    "required": ["schema", "corpus_id", "provenance", "generation", "schemas", "cases"],
    "properties": {
        "schema": {"const": CORPUS_SCHEMA_ID},
        "corpus_id": {"type": "string", "minLength": 1},
        "provenance": {
            "type": "object",
            "additionalProperties": False,
            "required": [
                "contains_customer_data",
                "fixture_origin",
                "conformance_basis",
                "recoverable_source",
            ],
            "properties": {
                "contains_customer_data": {"const": False},
                "fixture_origin": {"const": "independently-authored-synthetic"},
                "conformance_basis": {
                    "type": "array",
                    "minItems": 1,
                    "items": {"type": "string", "format": "uri"},
                },
                "recoverable_source": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["repository", "commit", "generator_path"],
                    "properties": {
                        "repository": {"const": RECOVERABLE_SOURCE["repository"]},
                        "commit": {"const": RECOVERABLE_SOURCE["commit"]},
                        "generator_path": {"const": RECOVERABLE_SOURCE["generator_path"]},
                    },
                },
            },
        },
        "generation": {
            "type": "object",
            "additionalProperties": False,
            "required": ["source_date_epoch", "gzip_mtime", "zip_entry_time", "zip_entry_order"],
            "properties": {
                "source_date_epoch": {"const": 0},
                "gzip_mtime": {"const": 0},
                "zip_entry_time": {"const": "1980-01-01T00:00:00Z"},
                "zip_entry_order": {"const": "manifest"},
            },
        },
        "schemas": {
            "type": "object",
            "additionalProperties": {"$ref": "#/$defs/fileReference"},
            "required": ["manifest", "expected_state"],
        },
        "cases": {"type": "array", "minItems": 1, "items": {"$ref": "#/$defs/case"}},
    },
    "$defs": {
        "fileReference": {
            "type": "object",
            "additionalProperties": False,
            "required": ["path", "sha256"],
            "properties": {
                "path": {"type": "string", "minLength": 1},
                "sha256": {"type": "string", "pattern": "^[0-9a-f]{64}$"},
            },
        },
        "payloadReference": {
            "type": "object",
            "additionalProperties": False,
            "required": ["path", "sha256", "filename", "media_type", "container"],
            "properties": {
                "path": {"type": "string", "pattern": "^payloads/.+"},
                "sha256": {"type": "string", "pattern": "^[0-9a-f]{64}$"},
                "filename": {"type": "string", "minLength": 1},
                "media_type": {"type": "string", "minLength": 1},
                "container": {"enum": ["plain", "gzip", "zip"]},
            },
        },
        "case": {
            "type": "object",
            "additionalProperties": False,
            "required": [
                "id",
                "source_case_id",
                "phase",
                "delivery_order",
                "validity",
                "semantic_group",
                "payloads",
                "expected",
                "expected_sha256",
                "expected_outcome",
            ],
            "properties": {
                "id": {"type": "string", "pattern": "^[a-z0-9][a-z0-9-]+$"},
                "source_case_id": {"type": "string", "pattern": "^[a-z0-9][a-z0-9-]+$"},
                "phase": {
                    "enum": ["baseline", "transport", "replay", "invalid", "isolation", "routing", "resource"]
                },
                "delivery_order": {"type": "integer", "minimum": 1},
                "validity": {"enum": ["conformant", "real_world_extension", "nonconformant", "non_report"]},
                "semantic_group": {"type": "string", "minLength": 1},
                "recovery_for": {"type": "string", "pattern": "^[a-z0-9][a-z0-9-]+$"},
                "payloads": {
                    "type": "array",
                    "minItems": 1,
                    "items": {"$ref": "#/$defs/payloadReference"},
                },
                "expected": {"type": "string", "pattern": "^expected/.+\\.json$"},
                "expected_sha256": {"type": "string", "pattern": "^[0-9a-f]{64}$"},
                "expected_outcome": {
                    "enum": ["inserted", "rejected", "duplicate"]
                },
                "routing_setup": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["owned_domains", "default_client_slug"],
                    "properties": {
                        "owned_domains": {"type": "object", "additionalProperties": {"type": "string"}},
                        "default_client_slug": {"type": "string"},
                    },
                },
            },
        },
    },
}


EXPECTED_STATE_SCHEMA: dict[str, Any] = {
    "$schema": "https://json-schema.org/draft/2020-12/schema",
    "$id": "https://dmarc-analyzer.example/schemas/conformance-expected-state-v1.json",
    "title": "DmarcAnalyzer normalized conformance expected state",
    "type": "object",
    "additionalProperties": False,
    "required": ["schema", "case_id", "outcome", "reason_class", "reports", "deltas"],
    "properties": {
        "schema": {"const": EXPECTED_SCHEMA_ID},
        "case_id": {"type": "string", "pattern": "^[a-z0-9][a-z0-9-]+$"},
        "recovery_for": {"type": "string", "pattern": "^[a-z0-9][a-z0-9-]+$"},
        "outcome": {"enum": ["inserted", "rejected", "duplicate"]},
        "reason_class": {
            "type": ["string", "null"],
            "enum": [None, "duplicate", "xml_malformed", "schema_invalid", "archive_invalid", "size_limit"],
        },
        "reports": {"type": "array", "items": {"$ref": "#/$defs/report"}},
        "deltas": {"$ref": "#/$defs/deltas"},
    },
    "$defs": {
        "authResult": {
            "type": "object",
            "additionalProperties": False,
            "required": ["domain", "result"],
            "properties": {
                "domain": {"type": "string"},
                "selector": {"type": ["string", "null"]},
                "scope": {"type": ["string", "null"]},
                "result": {"type": "string"},
            },
        },
        "reason": {
            "type": "object",
            "additionalProperties": False,
            "required": ["type", "comment"],
            "properties": {
                "type": {"type": "string"},
                "comment": {"type": ["string", "null"]},
            },
        },
        "record": {
            "type": "object",
            "additionalProperties": False,
            "required": [
                "source_ip",
                "message_count",
                "disposition",
                "dkim",
                "spf",
                "header_from",
                "envelope_from",
                "envelope_to",
                "reasons",
                "dkim_auth",
                "spf_auth",
            ],
            "properties": {
                "source_ip": {"type": "string"},
                "message_count": {"type": "integer"},
                "disposition": {"type": "string"},
                "dkim": {"type": "string"},
                "spf": {"type": "string"},
                "header_from": {"type": "string"},
                "envelope_from": {"type": ["string", "null"]},
                "envelope_to": {"type": ["string", "null"]},
                "reasons": {"type": "array", "items": {"$ref": "#/$defs/reason"}},
                "dkim_auth": {"type": "array", "items": {"$ref": "#/$defs/authResult"}},
                "spf_auth": {"type": "array", "items": {"$ref": "#/$defs/authResult"}},
            },
        },
        "report": {
            "type": "object",
            "additionalProperties": False,
            "required": ["key", "metadata", "policy", "records", "routing"],
            "properties": {
                "key": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["policy_domain", "report_id", "range_begin_epoch", "range_end_epoch"],
                    "properties": {
                        "policy_domain": {"type": "string"},
                        "report_id": {"type": "string"},
                        "range_begin_epoch": {"type": "integer"},
                        "range_end_epoch": {"type": "integer"},
                    },
                },
                "metadata": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["organization", "record_count"],
                    "properties": {
                        "organization": {"type": "string"},
                        "record_count": {"type": "integer", "minimum": 0},
                    },
                },
                "policy": {
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["p", "sp", "np", "pct", "adkim", "aspf", "testing", "discovery_method", "fo"],
                    "properties": {
                        "p": {"type": "string"},
                        "sp": {"type": ["string", "null"]},
                        "np": {"type": ["string", "null"]},
                        "pct": {"type": ["integer", "null"]},
                        "adkim": {"type": ["string", "null"]},
                        "aspf": {"type": ["string", "null"]},
                        "testing": {"type": ["string", "null"]},
                        "discovery_method": {"type": ["string", "null"]},
                        "fo": {"type": ["string", "null"]},
                    },
                },
                "records": {"type": "array", "items": {"$ref": "#/$defs/record"}},
                "routing": {
                    "oneOf": [
                        {"type": "null"},
                        {
                            "type": "object",
                            "additionalProperties": False,
                            "required": ["client_slug", "fallback_used"],
                            "properties": {
                                "client_slug": {"type": "string"},
                                "fallback_used": {"type": "boolean"},
                            },
                        },
                    ]
                },
            },
        },
        "counterMap": {
            "type": "object",
            "additionalProperties": {"type": "integer", "minimum": 0},
        },
        "deltas": {
            "type": "object",
            "additionalProperties": False,
            "required": ["reports", "records", "messages", "compliant_messages", "dispositions", "dkim", "spf"],
            "properties": {
                "reports": {"type": "integer", "minimum": 0},
                "records": {"type": "integer", "minimum": 0},
                "messages": {"type": "integer", "minimum": 0},
                "compliant_messages": {"type": "integer", "minimum": 0},
                "dispositions": {"$ref": "#/$defs/counterMap"},
                "dkim": {"$ref": "#/$defs/counterMap"},
                "spf": {"$ref": "#/$defs/counterMap"},
            },
        },
    },
}


def json_bytes(value: Any) -> bytes:
    return (json.dumps(value, indent=2, sort_keys=True, ensure_ascii=True) + "\n").encode("utf-8")


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def xml_tag(name: str, value: Any, indent: str) -> str:
    return f"{indent}<{name}>{escape(str(value))}</{name}>"


def normalized_record(
    source_ip: str,
    count: int,
    disposition: str,
    dkim: str,
    spf: str,
    header_from: str,
    *,
    envelope_from: str | None = None,
    envelope_to: str | None = None,
    reasons: Sequence[Mapping[str, str]] = (),
    dkim_auth: Sequence[Mapping[str, Any]] = (),
    spf_auth: Sequence[Mapping[str, Any]] = (),
) -> dict[str, Any]:
    return {
        "source_ip": source_ip,
        "message_count": count,
        "disposition": disposition,
        "dkim": dkim,
        "spf": spf,
        "header_from": header_from,
        "envelope_from": envelope_from,
        "envelope_to": envelope_to,
        "reasons": [dict(item) for item in reasons],
        "dkim_auth": [dict(item) for item in dkim_auth],
        "spf_auth": [dict(item) for item in spf_auth],
    }


def report_spec(
    report_id: str,
    domain: str,
    records: Sequence[Mapping[str, Any]],
    *,
    organization: str = "Synthetic Reporter",
    email: str = "reports@reporter.example",
    range_begin: int = FIXED_RANGE_BEGIN,
    range_end: int = FIXED_RANGE_END,
    policy: Mapping[str, Any] | None = None,
    namespaced: bool = False,
    routing: Mapping[str, Any] | None = None,
) -> dict[str, Any]:
    effective_policy = {
        "p": "none",
        "sp": None,
        "np": None,
        "pct": 100,
        "adkim": "r",
        "aspf": "r",
        "testing": None,
        "discovery_method": None,
        "fo": None,
    }
    if policy:
        effective_policy.update(policy)
    return {
        "report_id": report_id,
        "domain": domain,
        "organization": organization,
        "email": email,
        "range_begin": range_begin,
        "range_end": range_end,
        "policy": effective_policy,
        "records": [dict(record) for record in records],
        "namespaced": namespaced,
        "routing": dict(routing) if routing else None,
    }


def render_record(record: Mapping[str, Any]) -> list[str]:
    lines = ["  <record>", "    <row>"]
    lines.append(xml_tag("source_ip", record["source_ip"], "      "))
    lines.append(xml_tag("count", record["message_count"], "      "))
    lines.extend(["      <policy_evaluated>"])
    lines.append(xml_tag("disposition", record["disposition"], "        "))
    lines.append(xml_tag("dkim", record["dkim"], "        "))
    lines.append(xml_tag("spf", record["spf"], "        "))
    for reason in record["reasons"]:
        lines.append("        <reason>")
        lines.append(xml_tag("type", reason["type"], "          "))
        if reason.get("comment") is not None:
            lines.append(xml_tag("comment", reason["comment"], "          "))
        lines.append("        </reason>")
    lines.extend(["      </policy_evaluated>", "    </row>", "    <identifiers>"])
    if record["envelope_to"] is not None:
        lines.append(xml_tag("envelope_to", record["envelope_to"], "      "))
    if record["envelope_from"] is not None:
        lines.append(xml_tag("envelope_from", record["envelope_from"], "      "))
    lines.append(xml_tag("header_from", record["header_from"], "      "))
    lines.extend(["    </identifiers>", "    <auth_results>"])
    for result in record["dkim_auth"]:
        lines.append("      <dkim>")
        lines.append(xml_tag("domain", result["domain"], "        "))
        if result.get("selector") is not None:
            lines.append(xml_tag("selector", result["selector"], "        "))
        lines.append(xml_tag("result", result["result"], "        "))
        lines.append("      </dkim>")
    for result in record["spf_auth"]:
        lines.append("      <spf>")
        lines.append(xml_tag("domain", result["domain"], "        "))
        if result.get("scope") is not None:
            lines.append(xml_tag("scope", result["scope"], "        "))
        lines.append(xml_tag("result", result["result"], "        "))
        lines.append("      </spf>")
    lines.extend(["    </auth_results>", "  </record>"])
    return lines


def render_report(spec: Mapping[str, Any], *, include_report_id: bool = True, include_records: bool = True) -> bytes:
    namespace = ' xmlns="urn:ietf:params:xml:ns:dmarc-2.0"' if spec["namespaced"] else ""
    lines = ['<?xml version="1.0" encoding="UTF-8"?>', f"<feedback{namespace}>", "  <version>1.0</version>"]
    lines.extend(["  <report_metadata>", xml_tag("org_name", spec["organization"], "    ")])
    lines.append(xml_tag("email", spec["email"], "    "))
    if include_report_id:
        lines.append(xml_tag("report_id", spec["report_id"], "    "))
    lines.extend(["    <date_range>"])
    lines.append(xml_tag("begin", spec["range_begin"], "      "))
    lines.append(xml_tag("end", spec["range_end"], "      "))
    lines.extend(["    </date_range>", "  </report_metadata>", "  <policy_published>"])
    lines.append(xml_tag("domain", spec["domain"], "    "))
    policy = spec["policy"]
    if spec["namespaced"]:
        order = ("discovery_method", "p", "sp", "np", "fo", "adkim", "aspf", "testing", "pct")
    else:
        order = ("adkim", "aspf", "p", "sp", "pct", "fo", "np", "testing", "discovery_method")
    for name in order:
        value = policy.get(name)
        if value is not None:
            lines.append(xml_tag(name, value, "    "))
    lines.append("  </policy_published>")
    if include_records:
        for record in spec["records"]:
            lines.extend(render_record(record))
    lines.append("</feedback>")
    return ("\n".join(lines) + "\n").encode("utf-8")


def deterministic_gzip(payload: bytes) -> bytes:
    target = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", fileobj=target, compresslevel=9, mtime=0) as stream:
        stream.write(payload)
    return target.getvalue()


def deterministic_zip(entries: Sequence[tuple[str, bytes]]) -> bytes:
    target = io.BytesIO()
    with zipfile.ZipFile(target, mode="w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, payload in entries:
            info = zipfile.ZipInfo(name, ZIP_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, payload)
    return target.getvalue()


def attachment(filename: str, media_type: str, payload: bytes, container: str) -> dict[str, Any]:
    return {"filename": filename, "media_type": media_type, "payload": payload, "container": container}



def canonical_report(spec: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "key": {
            "policy_domain": spec["domain"].strip().lower(),
            "report_id": spec["report_id"].strip(),
            "range_begin_epoch": spec["range_begin"],
            "range_end_epoch": spec["range_end"],
        },
        "metadata": {
            "organization": spec["organization"],
            "record_count": len(spec["records"]),
        },
        "policy": dict(spec["policy"]),
        "records": [dict(record) for record in spec["records"]],
        "routing": spec["routing"],
    }


def deltas(reports: Sequence[Mapping[str, Any]]) -> dict[str, Any]:
    records = [record for report in reports for record in report["records"]]
    dispositions = {name: 0 for name in ("none", "pass", "quarantine", "reject")}
    dkim = {name: 0 for name in ("pass", "fail")}
    spf = {name: 0 for name in ("pass", "fail")}
    messages = 0
    compliant = 0
    for record in records:
        count = int(record["message_count"])
        messages += count
        disposition = str(record["disposition"]).lower()
        if disposition:
            dispositions[disposition] = dispositions.get(disposition, 0) + count
        dkim_result = str(record["dkim"]).lower()
        spf_result = str(record["spf"]).lower()
        if dkim_result:
            dkim[dkim_result] = dkim.get(dkim_result, 0) + count
        if spf_result:
            spf[spf_result] = spf.get(spf_result, 0) + count
        if dkim_result == "pass" or spf_result == "pass":
            compliant += count
    return {
        "reports": len(reports),
        "records": len(records),
        "messages": messages,
        "compliant_messages": compliant,
        "dispositions": dispositions,
        "dkim": dkim,
        "spf": spf,
    }


def expected_state(outcome: str, reports: Sequence[Mapping[str, Any]] = (), reason_class: str | None = None) -> dict[str, Any]:
    canonical = [canonical_report(report) for report in reports]
    return {"outcome": outcome, "reason_class": reason_class, "reports": canonical, "deltas": deltas(canonical)}


def expected(
    case_id: str,
    expected_state: Mapping[str, Any],
    *,
    recovery_for: str | None = None,
) -> dict[str, Any]:
    result = {"schema": EXPECTED_SCHEMA_ID, "case_id": case_id, **dict(expected_state)}
    if recovery_for is not None:
        result["recovery_for"] = recovery_for
    return result



def standard_records(domain: str = "alpha.example") -> list[dict[str, Any]]:
    return [
        normalized_record(
            "192.0.2.10",
            100,
            "none",
            "pass",
            "pass",
            domain,
            envelope_from=f"bounce.{domain}",
            dkim_auth=({"domain": domain, "selector": "s1", "result": "pass"},),
            spf_auth=({"domain": f"bounce.{domain}", "scope": "mfrom", "result": "pass"},),
        ),
        normalized_record(
            "198.51.100.12",
            12,
            "reject",
            "fail",
            "fail",
            domain,
            envelope_from="sender.third-party.example",
            dkim_auth=({"domain": domain, "selector": "s2", "result": "fail"},),
            spf_auth=({"domain": "sender.third-party.example", "scope": "mfrom", "result": "fail"},),
        ),
    ]


def report_filename(spec: Mapping[str, Any], suffix: str = ".xml") -> str:
    return f'receiver.example!{spec["domain"]}!{spec["range_begin"]}!{spec["range_end"]}{suffix}'


def case_definition(
    case_id: str,
    phase: str,
    order: int,
    validity: str,
    semantic_group: str,
    attachments: Sequence[Mapping[str, Any]],
    expected_state: Mapping[str, Any],
    *,
    routing_setup: Mapping[str, Any] | None = None,
    recovery_for: str | None = None,
) -> dict[str, Any]:
    result = {
        "id": case_id,
        "phase": phase,
        "delivery_order": order,
        "validity": validity,
        "semantic_group": semantic_group,
        "attachments": [dict(item) for item in attachments],
        "expected_object": expected(case_id, expected_state, recovery_for=recovery_for),
    }
    if routing_setup:
        result["routing_setup"] = dict(routing_setup)
    if recovery_for:
        result["recovery_for"] = recovery_for
    return result


ISOLATION_TARGETS = frozenset(
    {
        "null-report",
        "truncation-after-record",
        "truncation-mid-record",
        "missing-report-id",
        "no-records",
        "non-dmarc-payload",
        "corrupt-gzip",
        "archive-size-limit",
        "external-entity",
    }
)


def recovery_sentinel(target_case_id: str, sequence: int) -> dict[str, Any]:
    domain = f"recovery-{sequence:02d}.example"
    range_begin = FIXED_RANGE_BEGIN + (sequence * 86400)
    report = report_spec(
        f"recovery-{sequence:02d}-001",
        domain,
        standard_records(domain),
        range_begin=range_begin,
        range_end=range_begin + 86399,
    )
    return case_definition(
        f"sentinel-after-{target_case_id}",
        "isolation",
        0,
        "conformant",
        "post-failure-recovery",
        [attachment(report_filename(report), "text/xml", render_report(report), "plain")],
        expected_state("inserted", [report]),
        recovery_for=target_case_id,
    )


def build_case_definitions() -> list[dict[str, Any]]:
    cases: list[dict[str, Any]] = []
    baseline = report_spec("baseline-a-001", "alpha.example", standard_records())
    baseline_xml = render_report(baseline)
    cases.append(case_definition(
        "valid-v1-plain", "baseline", 10, "conformant", "baseline-a",
        [attachment(report_filename(baseline), "text/xml", baseline_xml, "plain")],
        expected_state("inserted", [baseline]),
    ))

    v2 = report_spec(
        "rfc9990-shape-001",
        "beta.example",
        [normalized_record(
            "203.0.113.25", 23, "pass", "pass", "fail", "beta.example",
            envelope_from="mail.beta.example",
            dkim_auth=({"domain": "beta.example", "selector": "blue", "result": "pass"},),
            spf_auth=({"domain": "mail.beta.example", "scope": "mfrom", "result": "fail"},),
        )],
        policy={"p": "quarantine", "sp": "none", "np": "reject", "testing": "n", "discovery_method": "treewalk", "pct": None},
        namespaced=True,
    )
    cases.append(case_definition(
        "valid-v2-namespaced", "baseline", 20, "conformant", "rfc9990-shape",
        [attachment(report_filename(v2), "text/xml", render_report(v2), "plain")],
        expected_state("inserted", [v2]),
    ))

    gzip_report = report_spec("gzip-001", "gamma.example", standard_records("gamma.example"))
    cases.append(case_definition(
        "valid-gzip", "transport", 30, "conformant", "gzip",
        [attachment(report_filename(gzip_report, ".xml.gz"), "application/gzip", deterministic_gzip(render_report(gzip_report)), "gzip")],
        expected_state("inserted", [gzip_report]),
    ))

    mislabeled = report_spec("gzip-misnamed-001", "delta.example", standard_records("delta.example"))
    cases.append(case_definition(
        "gzip-misnamed", "transport", 40, "real_world_extension", "gzip-misnamed",
        [attachment(report_filename(mislabeled, ".zip"), "application/zip", deterministic_gzip(render_report(mislabeled)), "gzip")],
        expected_state("inserted", [mislabeled]),
    ))

    zipped = report_spec("zip-single-001", "epsilon.example", standard_records("epsilon.example"))
    zipped_name = report_filename(zipped)
    cases.append(case_definition(
        "zip-single", "transport", 50, "real_world_extension", "zip-single",
        [attachment(report_filename(zipped, ".zip"), "application/zip", deterministic_zip([(zipped_name, render_report(zipped))]), "zip")],
        expected_state("inserted", [zipped]),
    ))

    junk_second = report_spec("zip-junk-first-001", "zeta.example", standard_records("zeta.example"))
    cases.append(case_definition(
        "zip-junk-first", "transport", 60, "real_world_extension", "zip-junk-first",
        [attachment(
            report_filename(junk_second, ".zip"), "application/zip",
            deterministic_zip([("00-readme.txt", b"synthetic archive marker\n"), (report_filename(junk_second), render_report(junk_second))]),
            "zip",
        )],
        expected_state("inserted", [junk_second]),
    ))

    zip_a = report_spec("zip-multi-a-001", "eta.example", standard_records("eta.example"))
    zip_b = report_spec("zip-multi-b-001", "theta.example", standard_records("theta.example"))
    cases.append(case_definition(
        "zip-two-valid", "transport", 70, "real_world_extension", "zip-two-valid",
        [attachment(
            "receiver.example!multi.example!1785888000!1785974399.zip", "application/zip",
            deterministic_zip([(report_filename(zip_a), render_report(zip_a)), (report_filename(zip_b), render_report(zip_b))]),
            "zip",
        )],
        expected_state("inserted", [zip_a, zip_b]),
    ))

    optional = report_spec(
        "optional-omitted-001", "iota.example", standard_records("iota.example"),
        policy={"sp": None, "pct": None, "adkim": None, "aspf": None},
    )
    cases.append(case_definition(
        "optional-fields-omitted", "baseline", 80, "conformant", "optional-fields",
        [attachment(report_filename(optional), "text/xml", b"\xef\xbb\xbf \n" + render_report(optional), "plain")],
        expected_state("inserted", [optional]),
    ))

    complex_report = report_spec(
        "ipv6-auth-001", "kappa.example",
        [normalized_record(
            "2001:db8::25", 41, "quarantine", "pass", "fail", "news.kappa.example",
            envelope_to="recipient.example", envelope_from="bounce.kappa.example",
            reasons=({"type": "local_policy", "comment": "Synthetic allow-list override"},),
            dkim_auth=(
                {"domain": "kappa.example", "selector": "primary", "result": "pass"},
                {"domain": "third-party.example", "selector": "relay", "result": "fail"},
            ),
        )],
        policy={"p": "quarantine", "sp": "reject", "adkim": "s"},
    )
    cases.append(case_definition(
        "ipv6-multi-dkim-override", "baseline", 90, "conformant", "complex-auth",
        [attachment(report_filename(complex_report), "text/xml", render_report(complex_report), "plain")],
        expected_state("inserted", [complex_report]),
    ))

    null_record = normalized_record("", 0, "", "", "", "lambda.example")
    null_report = report_spec("null-volume-001", "lambda.example", [null_record], policy={"p": "quarantine", "sp": "quarantine"})
    null_report_payload = render_report(null_report)
    # Analyzer deliberately maps absent policy_evaluated values to the safe
    # non-compliant defaults exposed by DmarcRua. Keep the raw empty-value case,
    # but pin the durable PostgreSQL projection to that runtime normalization.
    null_report["records"][0].update({"disposition": "none", "dkim": "fail", "spf": "fail"})
    cases.append(case_definition(
        "null-report", "isolation", 100, "real_world_extension", "null-report",
        [attachment(report_filename(null_report), "text/xml", null_report_payload, "plain")],
        expected_state("inserted", [null_report]),
    ))

    cases.append(case_definition(
        "replay-exact", "replay", 110, "conformant", "baseline-a",
        [attachment(report_filename(baseline), "text/xml", baseline_xml, "plain")],
        expected_state("duplicate", reason_class="duplicate"),
    ))
    cases.append(case_definition(
        "replay-cross-container", "replay", 120, "conformant", "baseline-a",
        [attachment(report_filename(baseline, ".xml.gz"), "application/gzip", deterministic_gzip(baseline_xml), "gzip")],
        expected_state("duplicate", reason_class="duplicate"),
    ))

    cross_domain = report_spec("baseline-a-001", "mu.example", standard_records("mu.example"))
    cases.append(case_definition(
        "report-id-cross-domain", "replay", 130, "conformant", "report-id-collision",
        [attachment(report_filename(cross_domain), "text/xml", render_report(cross_domain), "plain")],
        expected_state("inserted", [cross_domain]),
    ))

    conflicting = report_spec("baseline-a-001", "alpha.example", [
        normalized_record("192.0.2.10", 999, "none", "pass", "pass", "alpha.example")
    ])
    cases.append(case_definition(
        "conflicting-duplicate", "replay", 140, "nonconformant", "baseline-a",
        [attachment(report_filename(conflicting), "text/xml", render_report(conflicting), "plain")],
        expected_state("duplicate", reason_class="duplicate"),
    ))

    truncatable = report_spec("truncated-complete-001", "nu.example", [standard_records("nu.example")[0]])
    complete_xml = render_report(truncatable)
    after_record = complete_xml[: complete_xml.index(b"</record>") + len(b"</record>")] + b"\n"
    cases.append(case_definition(
        "truncation-after-record", "invalid", 150, "nonconformant", "truncation-complete-record",
        [attachment(report_filename(truncatable), "text/xml", after_record, "plain")],
        expected_state("inserted", [truncatable]),
    ))

    truncated_mid = report_spec("truncated-mid-001", "xi.example", [standard_records("xi.example")[0]])
    mid_xml = render_report(truncated_mid)
    cut = mid_xml.index(b"</count>") + len(b"</count>")
    cases.append(case_definition(
        "truncation-mid-record", "invalid", 160, "nonconformant", "truncation-mid-record",
        [attachment(report_filename(truncated_mid), "text/xml", mid_xml[:cut] + b"\n", "plain")],
        expected_state("rejected", reason_class="xml_malformed"),
    ))

    missing_id = report_spec("missing-id-placeholder", "omicron.example", standard_records("omicron.example"))
    cases.append(case_definition(
        "missing-report-id", "invalid", 170, "nonconformant", "missing-required-field",
        [attachment(report_filename(missing_id), "text/xml", render_report(missing_id, include_report_id=False), "plain")],
        expected_state("rejected", reason_class="schema_invalid"),
    ))

    no_records = report_spec("no-records-001", "pi.example", [])
    cases.append(case_definition(
        "no-records", "invalid", 180, "nonconformant", "missing-records",
        [attachment(report_filename(no_records), "text/xml", render_report(no_records, include_records=False), "plain")],
        expected_state("rejected", reason_class="schema_invalid"),
    ))

    cases.append(case_definition(
        "non-dmarc-payload", "isolation", 190, "non_report", "non-report",
        [attachment("looks-like-dmarc.xml", "application/xml", b"This is synthetic text, not an aggregate report.\n", "plain")],
        expected_state("rejected", reason_class="schema_invalid"),
    ))

    cases.append(case_definition(
        "corrupt-gzip", "invalid", 200, "nonconformant", "archive-invalid",
        [attachment(
            "receiver.example!rho.example!1785888000!1785974399.xml.gz",
            "application/gzip",
            CORRUPT_GZIP_PAYLOAD,
            "gzip",
        )],
        expected_state("rejected", reason_class="archive_invalid"),
    ))

    oversized = report_spec("expanded-size-001", "sigma.example", standard_records("sigma.example"))
    oversized_payload = render_report(oversized) + (b" " * (12 * 1024 * 1024))
    cases.append(case_definition(
        "archive-size-limit", "resource", 210, "real_world_extension", "expanded-size",
        [attachment(report_filename(oversized, ".xml.gz"), "application/gzip", deterministic_gzip(oversized_payload), "gzip")],
        expected_state("rejected", reason_class="size_limit"),
    ))

    external = report_spec("external-entity-001", "tau.example", standard_records("tau.example"))
    external_xml = render_report(external).replace(
        b"<feedback>",
        b'<!DOCTYPE feedback [<!ENTITY synthetic SYSTEM "http://invalid.example/entity">]>\n<feedback>',
        1,
    ).replace(b"Synthetic Reporter", b"&synthetic;", 1)
    cases.append(case_definition(
        "external-entity", "invalid", 220, "nonconformant", "external-entity",
        [attachment(report_filename(external), "text/xml", external_xml, "plain")],
        expected_state("rejected", reason_class="schema_invalid"),
    ))

    legacy_record = normalized_record(
        "203.0.113.88", 8, "none", "pass", "fail", "upsilon.example",
        spf_auth=({"domain": "upsilon.example", "scope": "helo", "result": "pass"},),
    )
    legacy = report_spec("legacy-enum-001", "upsilon.example", [legacy_record])
    legacy_xml = render_report(legacy).replace(b"<dkim>pass</dkim>", b"<dkim>PASS</dkim>", 1).replace(
        b"<spf>fail</spf>", b"<spf>Fail</spf>", 1
    )
    cases.append(case_definition(
        "legacy-enum-casing", "baseline", 230, "real_world_extension", "legacy-enums",
        [attachment(report_filename(legacy), "text/xml", legacy_xml, "plain")],
        expected_state("inserted", [legacy]),
    ))

    route_a = report_spec(
        "routing-a-001", "client-a.example", standard_records("client-a.example"),
        routing={"client_slug": "client-a", "fallback_used": False},
    )
    route_b = report_spec(
        "routing-b-001", "client-b.example", standard_records("client-b.example"),
        routing={"client_slug": "client-b", "fallback_used": False},
    )
    route_unknown = report_spec(
        "routing-default-001", "unowned.example", standard_records("unowned.example"),
        routing={"client_slug": "client-default", "fallback_used": True},
    )
    route_attachments = [
        attachment(report_filename(item), "text/xml", render_report(item), "plain")
        for item in (route_a, route_b, route_unknown)
    ]
    cases.append(case_definition(
        "routing-multi-client", "routing", 240, "conformant", "multi-client-routing",
        route_attachments,
        expected_state("inserted", [route_a, route_b, route_unknown]),
        routing_setup={
            "owned_domains": {"client-a.example": "client-a", "client-b.example": "client-b"},
            "default_client_slug": "client-default",
        },
    ))

    expanded: list[dict[str, Any]] = []
    sentinel_sequence = 0
    for item in cases:
        expanded.append(item)
        if item["id"] in ISOLATION_TARGETS:
            sentinel_sequence += 1
            expanded.append(recovery_sentinel(item["id"], sentinel_sequence))

    for sequence, item in enumerate(expanded, start=1):
        item["delivery_order"] = sequence * 10
    return expanded


def build_files() -> dict[str, bytes]:
    files: dict[str, bytes] = {
        "manifest.schema.json": json_bytes(MANIFEST_SCHEMA),
        "expected-state.schema.json": json_bytes(EXPECTED_STATE_SCHEMA),
    }
    manifest_cases: list[dict[str, Any]] = []
    for definition in build_case_definitions():
        case_id = definition["id"]
        expected_path = f"expected/{case_id}.json"
        expected_bytes = json_bytes(definition["expected_object"])
        files[expected_path] = expected_bytes

        payloads: list[dict[str, Any]] = []
        for index, item in enumerate(definition["attachments"], start=1):
            filename = item["filename"]
            if PurePosixPath(filename).name != filename:
                raise ValueError(f"payload filename is not a basename: {filename}")
            payload_path = f"payloads/{case_id}/{index:02d}-{filename}"
            payload_bytes = item["payload"]
            files[payload_path] = payload_bytes
            payloads.append(
                {
                    "path": payload_path,
                    "sha256": digest(payload_bytes),
                    "filename": filename,
                    "media_type": item["media_type"],
                    "container": item["container"],
                }
            )

        manifest_case = {
            key: definition[key]
            for key in ("id", "phase", "delivery_order", "validity", "semantic_group")
        }
        manifest_case.update(
            {
                "source_case_id": case_id,
                "payloads": payloads,
                "expected": expected_path,
                "expected_sha256": digest(expected_bytes),
                "expected_outcome": definition["expected_object"]["outcome"],
            }
        )
        if "routing_setup" in definition:
            manifest_case["routing_setup"] = definition["routing_setup"]
        if "recovery_for" in definition:
            manifest_case["recovery_for"] = definition["recovery_for"]
        manifest_cases.append(manifest_case)

    manifest = {
        "schema": CORPUS_SCHEMA_ID,
        "corpus_id": "dmarc-analyzer-synthetic-rfc9990-v1",
        "provenance": {
            "contains_customer_data": False,
            "fixture_origin": "independently-authored-synthetic",
            "conformance_basis": [
                "https://www.rfc-editor.org/rfc/rfc7489.html",
                "https://www.rfc-editor.org/rfc/rfc9990.html",
            ],
            "recoverable_source": RECOVERABLE_SOURCE,
        },
        "generation": {
            "source_date_epoch": 0,
            "gzip_mtime": 0,
            "zip_entry_time": "1980-01-01T00:00:00Z",
            "zip_entry_order": "manifest",
        },
        "schemas": {
            "manifest": {"path": "manifest.schema.json", "sha256": digest(files["manifest.schema.json"])},
            "expected_state": {
                "path": "expected-state.schema.json",
                "sha256": digest(files["expected-state.schema.json"]),
            },
        },
        "cases": manifest_cases,
    }
    files["manifest.json"] = json_bytes(manifest)
    validate_generated_files(files)
    return files



def validate_domain(value: str) -> None:
    if value and not value.lower().endswith(".example"):
        raise ValueError(f"non-reserved domain in corpus: {value}")


def validate_ip(value: str) -> None:
    if not value:
        return
    address = ipaddress.ip_address(value)
    if not any(address in network for network in DOCUMENTATION_NETWORKS):
        raise ValueError(f"non-documentation address in corpus: {value}")


def validate_identity_text(value: str, context: str) -> None:
    for match in DOMAIN_TOKEN.finditer(value):
        validate_domain(match.group())
    for match in IP_TOKEN.finditer(value):
        try:
            validate_ip(match.group())
        except ValueError as exc:
            raise ValueError(f"{context}: {exc}") from exc


def validate_identity_stream(stream: Any, context: str) -> None:
    decoder = codecs.getincrementaldecoder("utf-8-sig")()
    buffered = ""
    while chunk := stream.read(64 * 1024):
        buffered += decoder.decode(chunk)
        if len(buffered) > 512:
            validate_identity_text(buffered[:-256], context)
            buffered = buffered[-256:]
    buffered += decoder.decode(b"", final=True)
    validate_identity_text(buffered, context)


def validate_filename_identities(filename: str, context: str) -> None:
    stem = filename
    for suffix in (".gz", ".zip", ".xml", ".json", ".txt"):
        if stem.lower().endswith(suffix):
            stem = stem[: -len(suffix)]
    validate_identity_text(stem, context)


def validate_payload_identities(case_id: str, payload: Mapping[str, Any], data: bytes) -> None:
    filename = payload["filename"]
    validate_filename_identities(filename, f"{case_id}:{filename}")

    container = payload["container"]
    if container == "plain":
        validate_identity_stream(io.BytesIO(data), f"{case_id}:{filename}")
        return
    if container == "gzip":
        if case_id == "corrupt-gzip":
            if data != CORRUPT_GZIP_PAYLOAD:
                raise ValueError("corrupt-gzip must remain the fixed synthetic corruption marker")
            return
        try:
            with gzip.GzipFile(fileobj=io.BytesIO(data)) as expanded:
                validate_identity_stream(expanded, f"{case_id}:{filename}")
        except (EOFError, OSError) as exc:
            raise ValueError(f"generated gzip cannot be inspected: {case_id}") from exc
        return
    if container == "zip":
        try:
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                for entry in archive.infolist():
                    if entry.is_dir():
                        continue
                    validate_filename_identities(entry.filename, f"{case_id}:{entry.filename}")
                    with archive.open(entry) as expanded:
                        validate_identity_stream(expanded, f"{case_id}:{entry.filename}")
        except (OSError, UnicodeDecodeError, zipfile.BadZipFile) as exc:
            raise ValueError(f"generated ZIP cannot be inspected: {case_id}") from exc
        return
    raise ValueError(f"unexpected payload container: {container}")


def validate_generated_files(files: Mapping[str, bytes]) -> None:
    manifest = json.loads(files["manifest.json"])
    if manifest["schema"] != CORPUS_SCHEMA_ID:
        raise ValueError("unexpected corpus schema")
    if manifest["provenance"]["recoverable_source"] != RECOVERABLE_SOURCE:
        raise ValueError("recoverable source provenance drifted")
    cases = manifest["cases"]
    if len(cases) != 33 or len({case["id"] for case in cases}) != 33:
        raise ValueError("corpus must contain exactly 33 uniquely named cases")
    if [case["delivery_order"] for case in cases] != sorted(case["delivery_order"] for case in cases):
        raise ValueError("cases are not in delivery order")
    case_ids = {case["id"] for case in cases}
    if not ISOLATION_TARGETS.issubset(case_ids):
        raise ValueError("one or more isolation targets are missing")
    sentinels = [case for case in cases if "recovery_for" in case]
    if len(sentinels) != len(ISOLATION_TARGETS):
        raise ValueError("every isolation target must have exactly one recovery sentinel")

    sentinel_keys: list[tuple[str, str, int, int]] = []
    for index, case in enumerate(cases):
        if case["id"] in ISOLATION_TARGETS and (
            index + 1 >= len(cases) or cases[index + 1].get("recovery_for") != case["id"]
        ):
            raise ValueError(f"isolation target lacks an immediate recovery sentinel: {case['id']}")
        if case["source_case_id"] != case["id"]:
            raise ValueError(f"recoverable source case mismatch: {case['id']}")
        for payload in case["payloads"]:
            if digest(files[payload["path"]]) != payload["sha256"]:
                raise ValueError(f"payload hash mismatch: {case['id']}:{payload['path']}")
            validate_payload_identities(case["id"], payload, files[payload["path"]])

        expected_bytes = files[case["expected"]]
        if digest(expected_bytes) != case["expected_sha256"]:
            raise ValueError(f"expected-state hash mismatch: {case['id']}")
        expected_state = json.loads(expected_bytes)
        if expected_state["schema"] != EXPECTED_SCHEMA_ID or expected_state["case_id"] != case["id"]:
            raise ValueError(f"expected-state identity mismatch: {case['id']}")
        if expected_state["outcome"] != case["expected_outcome"]:
            raise ValueError(f"expected outcome mismatch: {case['id']}")
        if expected_state.get("recovery_for") != case.get("recovery_for"):
            raise ValueError(f"recovery sentinel linkage mismatch: {case['id']}")

        for report in expected_state["reports"]:
            validate_domain(report["key"]["policy_domain"])
            for record in report["records"]:
                validate_domain(record["header_from"])
                validate_domain(record["envelope_from"] or "")
                validate_domain(record["envelope_to"] or "")
                validate_ip(record["source_ip"])
                for auth in [*record["dkim_auth"], *record["spf_auth"]]:
                    validate_domain(auth["domain"])

    for sentinel in sentinels:
        expected_state = json.loads(files[sentinel["expected"]])
        if sentinel["expected_outcome"] != "inserted" or expected_state.get("recovery_for") != sentinel["recovery_for"]:
            raise ValueError(f"invalid recovery sentinel expectation: {sentinel['id']}")
        key = expected_state["reports"][0]["key"]
        sentinel_keys.append(
            (
                key["report_id"],
                key["policy_domain"],
                key["range_begin_epoch"],
                key["range_end_epoch"],
            )
        )
    if any(len({key[position] for key in sentinel_keys}) != len(sentinel_keys) for position in (0, 1)):
        raise ValueError("recovery sentinel report IDs and domains must be unique")
    if len({key[2:] for key in sentinel_keys}) != len(sentinel_keys):
        raise ValueError("recovery sentinel ranges must be unique")

    for schema_ref in manifest["schemas"].values():
        if digest(files[schema_ref["path"]]) != schema_ref["sha256"]:
            raise ValueError(f"schema hash mismatch: {schema_ref['path']}")

    payload_paths = {payload["path"] for case in cases for payload in case["payloads"]}
    referenced_paths = {
        "manifest.json",
        *(schema["path"] for schema in manifest["schemas"].values()),
        *(case["expected"] for case in cases),
        *payload_paths,
    }
    if referenced_paths != set(files):
        raise ValueError("manifest does not reference the complete generated corpus")
    if any(path.lower().endswith(".eml") for path in files):
        raise ValueError("Analyzer corpus must contain raw payloads, not EML envelopes")
    if sum(len(files[path]) for path in payload_paths) >= 900 * 1024:
        raise ValueError("raw corpus is unexpectedly large")
    all_bytes = b"\n".join(files.values()).lower()
    forbidden = (b"midtown", b"microsoft.com", b"google.com", b"yahoo.com", b"zoho.com")
    if any(token in all_bytes for token in forbidden):
        raise ValueError("corpus contains a forbidden non-synthetic identifier")



def write_files(output_root: Path, files: Mapping[str, bytes]) -> None:
    output_root.mkdir(parents=True, exist_ok=True)
    for relative, payload in sorted(files.items()):
        destination = output_root / PurePosixPath(relative)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(payload)


def tracked_files(output_root: Path) -> set[str]:
    if not output_root.exists():
        return set()
    return {
        path.relative_to(output_root).as_posix()
        for path in output_root.rglob("*")
        if path.is_file()
    }


def check_tree(output_root: Path, generated: Mapping[str, bytes] | None = None) -> None:
    expected_files = dict(generated or build_files())
    actual_paths = tracked_files(output_root)
    expected_paths = set(expected_files)
    missing = sorted(expected_paths - actual_paths)
    unexpected = sorted(actual_paths - expected_paths)
    changed = sorted(
        relative
        for relative in expected_paths & actual_paths
        if (output_root / PurePosixPath(relative)).read_bytes() != expected_files[relative]
    )
    if missing or unexpected or changed:
        details = []
        if missing:
            details.append(f"missing={','.join(missing)}")
        if unexpected:
            details.append(f"unexpected={','.join(unexpected)}")
        if changed:
            details.append(f"changed={','.join(changed)}")
        raise RuntimeError("corpus drift detected: " + " ".join(details))
    validate_generated_files({path: (output_root / PurePosixPath(path)).read_bytes() for path in expected_paths})


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT_ROOT, help="corpus output directory")
    parser.add_argument("--check", action="store_true", help="verify committed files without writing")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    files = build_files()
    if not args.check:
        write_files(args.output, files)
    check_tree(args.output, files)
    print(f"DmarcAnalyzer conformance corpus OK: 33 cases, {len(files)} files, root={args.output}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(1) from exc
