# Contributing to U-Vision

Issues that report bugs, gaps or inconsistencies are welcome. For anything larger than a small fix,
open an issue to agree on scope before writing code.

## Contributor License Agreement (CLA)

U-Vision is dual-licensed: AGPL-3.0 for open-source use, with a separate commercial license
available. Offering a commercial license depends on the project holding clear rights to every
contribution it ships.

**Every external code contribution requires a signed CLA before it can be merged.** The CLA does
not transfer ownership of your contribution; it grants the project the rights needed to distribute
it under both the open-source and the commercial license terms. Signing is a file you add in your
own pull request — no external service, no account. See [`CLA.md`](CLA.md) for the agreement and
the signing steps.

An automated check on each pull request looks for your signature file and tells you what to add if
it is missing. Everyone signs, members of the iyulab organization included; only bots such as
dependency updaters are exempt.

## No copyleft dependencies

U-Vision does not take on dependencies licensed under AGPL, GPL or LGPL. A copyleft dependency
would reach the commercial half of the dual license through the linked whole. Open an issue first
if you think an exception is warranted.

## Development

Build and test commands for the server (`server/`), the client (`client/`) and the introduction
site (`site/`) are in the [README](README.md). Run the checks for the part you changed before
opening a pull request.

## Reporting issues

Bug reports and design questions are welcome via GitHub Issues. For a potential security
vulnerability, do not open a public issue — contact iyulab privately first.
