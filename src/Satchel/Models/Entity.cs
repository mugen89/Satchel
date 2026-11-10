using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

/// <summary>Marks a string or byte[] property that is AES-GCM encrypted at rest.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EncryptedAttribute : Attribute;

/// <summary>Base for all stored entities. Entities are observable so the UI can bind to them directly.</summary>
public abstract partial class Entity : ObservableObject
{
    [Key]
    public int Id { get; set; }

    [ObservableProperty] private DateTime _createdAt = DateTime.Now;
    [ObservableProperty] private DateTime _updatedAt = DateTime.Now;

    /// <summary>Private items are left out of every list, search and widget unless "Show private items" is on.</summary>
    [ObservableProperty] private bool _isPrivate;

    [NotMapped] public bool IsNew => Id == 0;

    public void Touch() => UpdatedAt = DateTime.Now;

    partial void OnUpdatedAtChanged(DateTime value) => OnUpdatedAtChangedCore();

    protected virtual void OnUpdatedAtChangedCore() { }
}

/// <summary>Plain key/value pairs: app settings and (non-secret) key metadata. Never encrypted.</summary>
public class KeyValue
{
    [Key]
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
