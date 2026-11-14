using Serk.Clinic.Platform.Export;
using Serk.Clinic.SharedKernel;

namespace Serk.Clinic.Documents.Application;

public static class DocumentExports
{
    public static readonly ExportDefinition Register = new(
        "documents.register", "Documents", "Issued documents register",
        "Documents prepared or issued: reference, template, status, paper, PDF fingerprint. No clinical content or names.",
        [Capabilities.AuditRead], false, true,
        ["created_at", "verification_ref", "template", "template_version", "status", "paper", "finalized_at", "pdf_sha256", "pdf_bytes"],
        """
        SELECT created_at, verification_ref, template_code, template_version, status, paper_size, finalized_at, pdf_sha256, pdf_size_bytes
        FROM documents.document_versions
        WHERE tenant_id = @t AND facility_id = @f AND created_at >= @from_utc AND created_at < @to_utc
        ORDER BY created_at LIMIT @limit
        """);
}
