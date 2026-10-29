# Security Policy

## Supported versions

Security fixes are shipped in the latest release line. Please reproduce on the
newest release before reporting an issue.

| Version | Supported |
| --- | --- |
| 3.11.x | Yes |
| Older versions | No - upgrade to 3.11.x first |

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security
problem.** Use GitHub's private vulnerability reporting instead:

<https://github.com/purelogiccode/CHDStudio/security/advisories/new>

That form creates a private advisory visible only to the maintainers. If you
cannot use it, email the maintainer through
<https://www.purelogiccode.com/> and say that the message is a security report.

Please include:

- a description of the vulnerability and its impact;
- the affected version, operating system and architecture;
- steps to reproduce, with a minimal sample file or archive when possible;
- any proof-of-concept, logs or crash dumps;
- whether you intend to publish the finding, and your preferred credit name.

## What to expect

- We acknowledge a report within 3 business days.
- We confirm the affected versions and severity within 10 business days.
- We keep you informed while a fix is prepared and coordinate a disclosure
  date with you. Critical issues are fixed as a priority release; lower
  severity issues are batched into the next scheduled release.
- We credit reporters in the advisory unless you ask to stay anonymous.

## Scope

In scope:

- the CHD Studio desktop application and its installer/release zips;
- the `PBPSharp`, `CSOSharp`, `CCDSharp`, `MDSSharp` and `ISZSharp` libraries;
- the repository's build, test and release workflows.

Out of scope:

- vulnerabilities in third-party tools bundled with the app (`chdman`,
  `7za.exe`, `7zz`). Report those upstream; we will update the bundled build
  when a fixed version is available. A packaging problem that lets a bundled
  binary escape its intended use *is* in scope.
- vulnerabilities that require the attacker to already control the user's
  machine, account or input files in a way that is documented as trusted.
- reports produced only by an automated scanner without a reproducible impact.

## Safe harbor

We will not pursue legal action for good-faith security research that follows
this policy, avoids privacy violations, data destruction and service
disruption, and gives us reasonable time to fix the issue before publication.
