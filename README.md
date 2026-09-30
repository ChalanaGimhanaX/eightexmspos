# Enightx POS

Enightx POS is a prototype workspace for a Windows point-of-sale system aimed at small retail shops. The repository includes product planning documents plus early implementation work for a WPF desktop terminal and a FastAPI backend.

## What Is Included

- WPF desktop client for cashier login, product search, billing, payment, refunds, shift reports, customer management, goods receiving, and software update prompts.
- FastAPI backend for health checks, device management, sync workflows, and update endpoints.
- Shared contracts and planning documents for first-release behavior, delivery scope, and acceptance checks.
- Multilingual desktop resources for English, Sinhala, and Tamil UI text.

## Repository Structure

```text
apps/
|-- api/                 # FastAPI sync and device-management API
`-- desktop/             # WPF POS desktop application
contracts/               # Shared data contracts and API notes
docs/                    # Product specification and delivery plan
infra/                   # Deployment and infrastructure notes
tests/                   # Test workspace
```

## Technology

| Layer | Tools |
| --- | --- |
| Desktop | C#, WPF, .NET |
| API | Python, FastAPI, SQLAlchemy |
| Data and sync | Local desktop storage plus backend sync endpoints |
| Operations | Device registration, update checks, and deployment planning |

## Current Status

This is an active prototype, not a production POS release. The implementation already includes desktop screens and backend endpoints, while the documents describe the intended first release and remaining delivery checks.

Useful starting points:

- [First-release specification](docs/first-release-specification.md)
- [Technical design](docs/technical-design.md)
- [Delivery plan and acceptance checks](docs/delivery-plan.md)

## Local Development

Build the desktop workspace from the solution file:

```powershell
dotnet build EnightxPos.sln
```

Run the API from the `apps/api` workspace after installing its Python dependencies:

```powershell
cd apps/api
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -e .
python -m enightx_api.main
```

Review the docs before treating any workflow as complete. The project is still being shaped around real POS requirements.
