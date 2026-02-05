using System;

namespace UnrealSharp.Attributes;

[Flags]
public enum PropertyHookFlags
{
    None = 0,
    OnGetter = 1 << 0,
    OnSetter = 1 << 1
}

/// <summary>
/// Generates hook methods for property getters and setters.
/// When applied, generates partial methods: On{PropertyName}Get and On{PropertyName}Set
/// 
/// Example:
/// [UProperty(PropertyFlags.BlueprintReadWrite)]
/// [PropertyHook(PropertyHookFlags.OnSetter)]
/// public partial float HeightOverride { get; set; }
/// 
/// // Implement the generated hook:
/// partial void OnHeightOverrideSet(float value)
/// {
///     SizeBox.HeightOverride = value;
/// }
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class PropertyHookAttribute : Attribute
{
    public PropertyHookFlags Flags { get; }
    
    public PropertyHookAttribute(PropertyHookFlags flags)
    {
        Flags = flags;
    }
}
