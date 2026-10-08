if (-not ('ProxyHarbor.SourceAudit.SourceEndpointSample' -as [type])) {
    # Compile the shared production CSV reader rather than a second CSV parser.
    Add-Type -Path @(
        (Join-Path $PSScriptRoot 'SourceEndpointSample.cs'),
        (Join-Path $PSScriptRoot '../src/ProxyHarbor.Infrastructure/BoundedCsvReader.cs')
    )
}
