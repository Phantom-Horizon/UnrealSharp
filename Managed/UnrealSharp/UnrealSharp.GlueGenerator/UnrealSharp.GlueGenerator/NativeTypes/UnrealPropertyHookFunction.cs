using Microsoft.CodeAnalysis;
using UnrealSharp.GlueGenerator.NativeTypes.Properties;

namespace UnrealSharp.GlueGenerator.NativeTypes;

/// <summary>
/// Represents a getter/setter function generated for PropertyHook attributes.
/// Unlike UnrealGetterSetterFunction which wraps user-defined custom accessors,
/// this class generates functions that access the property and trigger hooks.
/// </summary>
public record UnrealPropertyHookFunction : UnrealFunctionBase
{
    // Store only the needed info to avoid circular reference with UnrealProperty
    private readonly string _propertySourceName;
    private readonly string _propertyManagedType;
    private readonly string _propertyMarshallerType;
    private readonly bool _isGetter;
    
    public UnrealPropertyHookFunction(UnrealProperty property, bool isGetter) 
        : base(
            isGetter ? EFunctionFlags.BlueprintPure : EFunctionFlags.None,
            isGetter ? $"Get{property.SourceName}" : $"Set{property.SourceName}",
            property.Namespace,
            Accessibility.Private,
            property.AssemblyName,
            property.Outer)
    {
        _propertySourceName = property.SourceName;
        _propertyManagedType = property.ManagedType.ToString();
        _propertyMarshallerType = property.MarshallerType;
        _isGetter = isGetter;
        ReturnType = new VoidProperty(this);
    }

    public override void ExportType(GeneratorStringBuilder builder, SourceProductionContext spc)
    {
        // Generate the Invoke method directly without backing variables
        builder.AppendEditorBrowsableAttribute();
        builder.AppendLine($"void Invoke_{SourceName}(IntPtr buffer, IntPtr returnBuffer)");
        builder.OpenBrace();
        
        if (_isGetter)
        {
            // Getter: read from property, write to returnBuffer
            builder.AppendLine($"{_propertyManagedType} returnValue = {_propertySourceName};");
            // Write directly to returnBuffer without offset
            builder.Append($"{_propertyMarshallerType}.ToNative(returnBuffer, 0, returnValue);");
        }
        else
        {
            // Setter: read from buffer directly without offset, write to property
            builder.Append($"{_propertyManagedType} value = {_propertyMarshallerType}.FromNative(buffer, 0);");
            builder.AppendLine($"{_propertySourceName} = value;");
        }
        
        builder.CloseBrace();
    }
}
