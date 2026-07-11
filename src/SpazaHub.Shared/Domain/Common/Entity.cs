namespace SpazaHub.Domain.Common;

/// <summary>
/// Base class for all persisted entities. Primary keys are client-generated GUID v7
/// so records can be created fully offline on any device.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; set; } = GuidV7.NewGuid();
}
