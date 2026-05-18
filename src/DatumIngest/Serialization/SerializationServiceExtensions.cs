using DatumIngest.Serialization;
using DatumIngest.Serialization.Csv;
using DatumIngest.Serialization.Fits;
using DatumIngest.Serialization.Idx;
using DatumIngest.Serialization.Json;
using DatumIngest.Serialization.Tar;
using DatumIngest.Serialization.Zip;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering file format implementations
/// in an <see cref="IServiceCollection"/>.
/// </summary>
public static class SerializationServiceExtensions
{
    /// <summary>
    /// Registers all built-in <see cref="IFileFormat"/> implementations as transient services.
    /// Resolve them as <c>IEnumerable&lt;IFileFormat&gt;</c> to feed into
    /// <see cref="DatumIngest.Serialization.FormatRegistry"/>.
    /// </summary>
    public static IServiceCollection AddFileFormats(this IServiceCollection services)
    {
        services.AddTransient<IFileFormat, CsvFileFormat>();
        services.AddTransient<IFileFormat, JsonFileFormat>();
        services.AddTransient<IFileFormat, JsonLinesFileFormat>();
        //services.AddTransient<IFileFormat, ParquetFileFormat>();
        //services.AddTransient<IFileFormat, Hdf5FileFormat>();
        services.AddTransient<IFileFormat, IdxFileFormat>();
        services.AddTransient<IFileFormat, FitsFileFormat>();
        services.AddTransient<IFileFormat, ZipFileFormat>();
        services.AddTransient<IFileFormat, TarFileFormat>();

        return services;
    }
}
