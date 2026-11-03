# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Common Development Commands

**Run the application:**
```bash
dotnet run
```
The application runs on HTTP (port 5062) and HTTPS (port 7069) by default.

**Database operations:**
```bash
dotnet ef migrations add <MigrationName>
dotnet ef database update
```

**Build and publish:**
```bash
dotnet build
dotnet publish
```

## Architecture Overview

This is an ASP.NET Core 8.0 Web API for AI-powered career coaching and CV analysis. The application uses minimal APIs with dependency injection and integrates with OpenAI/GPT models via Microsoft Semantic Kernel.

### Core Components

**Entry Point**: `Program.cs` - Contains all API endpoint definitions and service configuration using minimal API approach.

**Domain Models**: 
- `Domain/Goal.cs` - Career goals with 90-day horizon default
- `Domain/NextAction.cs` - Action items linked to goals with effort tracking
- Both use Guid primary keys and have one-to-many relationship

**Data Layer**: `Infra/Context.cs` - EF Core DbContext with PostgreSQL backend. Uses Supabase in production, localhost in development. Database migrations are auto-applied on startup.

**Request/Response Models**: `Models/Models.cs` - Contains DTOs for API communication including ChatMessage, PlanRequest, and CoachStreamRequest.

### Key Features

1. **CV Analysis** (`/api/cv/analyze`) - Multi-format document parsing (PDF via PdfPig, DOCX via OpenXml, plain text) with Brazilian Portuguese content processing
2. **Career Roadmaps** (`/api/roadmap/from-cv`) - AI-generated personalized development plans
3. **AI Coach Streaming** (`/api/coach/stream`) - Real-time coaching with server-sent events
4. **Goals Management** (`/api/goals`) - CRUD operations for career objectives

### AI Integration

The application uses dual AI services ("extract" and "synth") configured in appsettings with different GPT models. Features include:
- Token-based rate limiting (TPM configuration)
- Chunked text processing with sliding windows
- Brazilian Portuguese date normalization
- Streaming responses for real-time interactions

### Configuration

Configuration uses standard .NET patterns with environment-specific appsettings files:
- Production: Supabase PostgreSQL connection
- Development: Local PostgreSQL
- OpenAI model configuration in `OpenAI` section
- Analysis limits in `Analyze` section

### Development Notes

- Entity Framework migrations auto-run on startup
- CORS is enabled for all origins in development
- Swagger/OpenAPI documentation available at `/swagger`
- All API endpoints return JSON with proper error handling
- File uploads limited to PDF, DOCX, and TXT formats