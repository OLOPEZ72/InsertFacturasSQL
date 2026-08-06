using InsertFacturasSQL.Models;

namespace InsertFacturasSQL.Services;

public interface IDocumentReader
{
    DocumentReadResult Read(string filePath);
}
