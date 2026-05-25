using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Csv;
using Heliosoph.DatumV.Serialization.Fits;
using Heliosoph.DatumV.Serialization.Hdf5;
using Heliosoph.DatumV.Serialization.Idx;
using Heliosoph.DatumV.Serialization.Json;
using Heliosoph.DatumV.Serialization.Tar;
using Heliosoph.DatumV.Serialization.Zip;

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
    /// <see cref="Heliosoph.DatumV.Serialization.FormatRegistry"/>.
    /// </summary>
    public static IServiceCollection AddFileFormats(this IServiceCollection services)
    {
        services.AddTransient<IFileFormat, CsvFileFormat>();
        services.AddTransient<IFileFormat, JsonFileFormat>();
        services.AddTransient<IFileFormat, JsonLinesFileFormat>();
        //services.AddTransient<IFileFormat, ParquetFileFormat>();
        services.AddTransient<IFileFormat, Hdf5FileFormat>();
        services.AddTransient<IFileFormat, IdxFileFormat>();
        services.AddTransient<IFileFormat, FitsFileFormat>();
        services.AddTransient<IFileFormat, ZipFileFormat>();
        services.AddTransient<IFileFormat, TarFileFormat>();

        return services;
    }
}
