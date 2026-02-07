using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;

namespace UnrealSharp.GlueGenerator.NativeTypes.Properties;

public readonly record struct PropertyMethod
{
    public PropertyMethod(Accessibility accessibility, UnrealFunction? customPropertyMethod = null)
    {
        Accessibility = accessibility;
        CustomPropertyMethod = customPropertyMethod;
    }
    
    public bool HasCustomMethod => CustomPropertyMethod != null;
    
    public readonly Accessibility Accessibility;
    public readonly UnrealFunction? CustomPropertyMethod;
}

[Inspector]
public record UnrealProperty : UnrealType
{
    // Constants
    private const string UPropertyAttributeName = "UPropertyAttribute";
    private const EPropertyFlags InstancedFlags = EPropertyFlags.InstancedReference | EPropertyFlags.ExportObject;

    // General property configuration
    public EPropertyFlags PropertyFlags = EPropertyFlags.None;
    public bool DefaultComponent;
    public bool IsRootComponent;
    public string AttachmentComponent = string.Empty;
    public string AttachmentSocket = string.Empty;
    public string ReplicatedUsing = string.Empty;
    public ELifetimeCondition LifetimeCondition = ELifetimeCondition.None;

    // Immutable metadata
    public readonly bool IsPartial = true;
    public readonly bool IsNullable;
    public readonly bool IsRequired;
    
    // PropertyHook metadata
    public bool HasOnGetter;
    public bool HasOnSetter;
    
    // PropertyHook generated functions (for JSON serialization)
    private UnrealPropertyHookFunction? _hookGetterFunction;
    private UnrealPropertyHookFunction? _hookSetterFunction;

    // Type and marshaling information
    public PropertyType PropertyType = PropertyType.Unknown;
    public FieldName ManagedType;
    public RefKind ReferenceKind;

    public bool CanInstanceMarshallerBeStatic = false;
    
    public virtual string MarshallerType => throw new NotImplementedException();
    public virtual bool NeedsCachedMarshaller => false;
    public virtual bool NeedsBackingNativeProperty => false;
    public virtual bool IsBlittable => false;

    // Getter / Setter info
    public PropertyMethod? GetterMethod;
    public PropertyMethod? SetterMethod;

    // Codegen variables
    public string OffsetVariable => $"{Outer!.SourceName}_{SourceName}_Offset";
    public string NativePropertyVariable => $"{Outer!.SourceName}_{SourceName}_Property";
    public string InstancedMarshallerVariable => $"{Outer!.SourceName}_{SourceName}_Marshaller";

    protected string ToNative => ".ToNative";
    protected string FromNative => ".FromNative";

    public string CallToNative => MarshallerType + ToNative;
    public string CallFromNative => MarshallerType + FromNative;
    
    public virtual string NullValue => $"default({ManagedType})";

    // Parameter helpers
    public string GetParameterDeclaration() => $"{ReferenceKind.RefKindToString()}{ManagedType}{(IsNullable ? "?" : string.Empty)} {SourceName}";
    public string GetParameterCall() => $"{ReferenceKind.RefKindToString()}{SourceName}";
    
    public UnrealProperty(ISymbol memberSymbol, ITypeSymbol typeSymbol, PropertyType propertyType, UnrealType outer, SyntaxNode? syntaxNode = null) : base(memberSymbol, outer, syntaxNode)
    {
        PropertyType = propertyType;
        Namespace = typeSymbol.GetNamespace();
        IsNullable = typeSymbol.NullableAnnotation == NullableAnnotation.Annotated;
        
        if (syntaxNode is PropertyDeclarationSyntax propertyDeclarationSyntax)
        {
            IPropertySymbol propertySymbol = (IPropertySymbol) memberSymbol;
            GetterMethod = propertySymbol.GetPropertyMethodInfo(this, propertyDeclarationSyntax, propertySymbol.GetMethod);
            SetterMethod = propertySymbol.GetPropertyMethodInfo(this, propertyDeclarationSyntax, propertySymbol.SetMethod);
            IsRequired = propertySymbol.IsRequired;
        }
    }
    
    public UnrealProperty(PropertyType propertyType, UnrealType outer) : base(outer)
    {
        PropertyType = propertyType;
    }
    
    public UnrealProperty(PropertyType type, string sourceName, Accessibility accessibility, UnrealType outer) 
        : base(sourceName, outer.Namespace, accessibility, outer.AssemblyName, outer)
    {
        PropertyType = type;
        IsPartial = false;
        GetterMethod = new PropertyMethod(Accessibility.NotApplicable);
        SetterMethod = new PropertyMethod(Accessibility.NotApplicable);
    }
    
    [Inspect("UnrealSharp.Attributes.UPropertyAttribute", "UPropertyAttribute")]
    public static UnrealType UPropertyAttribute(UnrealType? outer, SyntaxNode? syntaxNode, GeneratorAttributeSyntaxContext ctx, ISymbol symbol, IReadOnlyList<AttributeData> attributes)
    {
        UnrealStruct owningStruct = (UnrealStruct) outer!;
        UnrealProperty property = PropertyFactory.CreateProperty(symbol, outer!, syntaxNode);
        
        // Check for PropertyHook attribute
        foreach (var attribute in symbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == "PropertyHookAttribute")
            {
                if (attribute.ConstructorArguments.Length > 0)
                {
                    var flags = (int)(attribute.ConstructorArguments[0].Value ?? 0);
                    property.HasOnGetter = (flags & 1) != 0; // PropertyHookFlags.OnGetter
                    property.HasOnSetter = (flags & 2) != 0; // PropertyHookFlags.OnSetter
                    
                    // Create hook functions for JSON serialization
                    property.CreateHookFunctions();
                }
            }
        }
        
        owningStruct.Properties.List.Add(property);
        return property;
    }
    
    [InspectArgument(["PropertyFlags", "flags"], UPropertyAttributeName)]
    public static void PropertyFlagsSpecifier(UnrealType topType, TypedConstant flags)
    {
        UnrealProperty property = (UnrealProperty)topType;
        EPropertyFlags flagValue = (EPropertyFlags)(flags.Value ?? EPropertyFlags.None);
        property.PropertyFlags |= flagValue;
            
        UnrealClass? outerClass = topType.Outer as UnrealClass;
            
        if (property.PropertyFlags.HasFlag(EPropertyFlags.PersistentInstance))
        {
            property.PropertyFlags |= InstancedFlags;
            property.AddEditInlineMeta();

            if (outerClass != null)
            {
                outerClass.ClassFlags |= EClassFlags.HasInstancedReference;
            }
        }
            
        if (property.PropertyFlags.HasFlag(EPropertyFlags.Config) && outerClass != null)
        {
            outerClass.ClassFlags |= EClassFlags.Config;
        }
    }
    
    [InspectArgument("DefaultComponent", UPropertyAttributeName)]
    public static void DefaultComponentSpecifier(UnrealType topType, TypedConstant defaultComponent)
    {
        UnrealProperty property = (UnrealProperty)topType;
        property.DefaultComponent = (bool)defaultComponent.Value!;
        property.PropertyType = PropertyType.DefaultComponent;
        property.PropertyFlags |= EPropertyFlags.BlueprintVisible | EPropertyFlags.NonTransactional | EPropertyFlags.InstancedReference;
        property.AddEditInlineMeta();
    }
    
    [InspectArgument("RootComponent", UPropertyAttributeName)]
    public static void RootComponentSpecifier(UnrealType topType, TypedConstant rootComponent)
    {
        UnrealProperty property = (UnrealProperty)topType;
        property.IsRootComponent = (bool)rootComponent.Value!;
    }
    
    [InspectArgument("AttachmentComponent", UPropertyAttributeName)]
    public static void AttachmentComponentSpecifier(UnrealType topType, TypedConstant attachmentComponent)
    {
        UnrealProperty property = (UnrealProperty)topType;
        property.AttachmentComponent = (string)attachmentComponent.Value!;
    }
    
    [InspectArgument("AttachmentSocket", UPropertyAttributeName)]
    public static void AttachmentSocketSpecifier(UnrealType topType, TypedConstant attachmentSocket)
    {
        UnrealProperty property = (UnrealProperty)topType;
        property.AttachmentSocket = (string)attachmentSocket.Value!;
    }
    
    [InspectArgument("ReplicatedUsing", UPropertyAttributeName)]
    public static void ReplicatedUsingSpecifier(UnrealType topType, TypedConstant replicatedUsing)
    {
        UnrealProperty property = (UnrealProperty)topType;
        property.ReplicatedUsing = (string)replicatedUsing.Value!;
        property.PropertyFlags |= EPropertyFlags.RepNotify | EPropertyFlags.Net;
    }
    
    [InspectArgument("LifetimeCondition", UPropertyAttributeName)]
    public static void LifetimeConditionSpecifier(UnrealType topType, TypedConstant lifetimeCondition)
    {
        UnrealProperty property = (UnrealProperty)topType;
        property.LifetimeCondition = (ELifetimeCondition)lifetimeCondition.Value!;
    }
    
    [InspectArgument("Category", UPropertyAttributeName)]
    public static void CategorySpecifier(UnrealType topType, TypedConstant category)
    {
        UnrealProperty property = (UnrealProperty)topType;
        property.AddMetaData("Category", (string)category.Value!);
    }
    
    /// <summary>
    /// Creates hook functions for PropertyHook attributes.
    /// These functions are used by UE to access the property through C# code,
    /// which triggers the hook methods.
    /// Note: We always create both getter and setter functions because C++ side
    /// requires both to be present (TCSGetterSetterProperty).
    /// </summary>
    public void CreateHookFunctions()
    {
        // Always create both getter and setter when any hook is enabled
        // because C++ TCSGetterSetterProperty requires both functions
        if (HasOnGetter || HasOnSetter)
        {
            _hookGetterFunction = new UnrealPropertyHookFunction(this, isGetter: true);
            _hookSetterFunction = new UnrealPropertyHookFunction(this, isGetter: false);
        }
    }

    public override void ExportType(GeneratorStringBuilder builder, SourceProductionContext spc)
    {
        ExportBackingVariables(builder);
        builder.AppendLine();
        
        // Note: PropertyHook does not generate partial method declarations
        // User should implement: private void On{PropertyName}Set({Type} value)
        // or: private {Type} On{PropertyName}Get({Type} value)
        
        if (this.HasCustomGetterOrSetter())
        {
            if (SetterMethod.HasCustomPropertyMethod())
            {
                SetterMethod!.Value.CustomPropertyMethod!.ExportType(builder, spc);
            }

            if (GetterMethod.HasCustomPropertyMethod())
            {
                GetterMethod!.Value.CustomPropertyMethod!.ExportType(builder, spc);
            }
            
            return;
        }
        
        string nullableSign = IsNullable ? "?" : string.Empty;
        string partialDeclaration = IsPartial ? "partial " : string.Empty;
        string isRequiredSign = IsRequired ? "required " : string.Empty;
        
        builder.AppendLine($"{TypeAccessibility.AccessibilityToString()}{isRequiredSign}{partialDeclaration}{ManagedType}{nullableSign} {SourceName}");
        builder.OpenBrace();
        
        if (GetterMethod != null)
        {
            builder.AppendGet(GetterMethod.Value.Accessibility);
            ExportGetter(builder);
        }
        
        if (SetterMethod != null)
        {
            builder.AppendSet(SetterMethod.Value.Accessibility);
            ExportSetter(builder);
        }
        
        builder.CloseBrace();
        
        // Export PropertyHook functions
        _hookGetterFunction?.ExportType(builder, spc);
        _hookSetterFunction?.ExportType(builder, spc);
    }

    protected virtual void ExportGetter(GeneratorStringBuilder builder)
    {
        if (HasOnGetter)
        {
            builder.AppendLine();
            builder.OpenBrace();
            builder.Append($"{ManagedType} __value = ");
            ExportFromNative(builder, SourceGenUtilities.NativeObject);
            builder.AppendLine($"return On{SourceName}Get(__value);");
            builder.CloseBrace();
        }
        else
        {
            builder.Append(" => ");
            ExportFromNative(builder, SourceGenUtilities.NativeObject);
        }
    }
    
    protected virtual void ExportSetter(GeneratorStringBuilder builder)
    {
        if (HasOnSetter)
        {
            builder.AppendLine();
            builder.OpenBrace();
            ExportToNative(builder, SourceGenUtilities.NativeObject, SourceGenUtilities.ValueParam);
            builder.AppendLine($"On{SourceName}Set({SourceGenUtilities.ValueParam});");
            builder.CloseBrace();
        }
        else
        {
            builder.Append(" => ");
            ExportToNative(builder, SourceGenUtilities.NativeObject, SourceGenUtilities.ValueParam);
        }
    }

    public override void ExportBackingVariables(GeneratorStringBuilder builder)
    {
        string offsetCode = $"static int {OffsetVariable}";
        
        if (NeedsBackingNativeProperty || NeedsCachedMarshaller)
        {
            ExportNativeProperty(builder);
        }
        
        if (NeedsCachedMarshaller)
        {
            string staticModifier = CanInstanceMarshallerBeStatic ? "static " : string.Empty;
            builder.AppendNewBackingField($"{staticModifier}{MarshallerType}? {InstancedMarshallerVariable};");
            builder.AppendNewBackingField($"{offsetCode};");
        }
        else
        {
            builder.AppendNewBackingField($"{offsetCode};");
        }
    }

    public override void ExportBackingVariablesToStaticConstructor(GeneratorStringBuilder builder, string nativeType)
    {
        if (NeedsBackingNativeProperty || NeedsCachedMarshaller)
        {
            builder.AppendLine($"{NativePropertyVariable} = CallGetNativePropertyFromName({nativeType}, \"{SourceName}\");"); 
        }
        
        if (NeedsCachedMarshaller)
        {
            builder.AppendLine($"{OffsetVariable} = CallGetPropertyOffset({NativePropertyVariable});");
        }
        else
        {
            builder.AppendLine($"{OffsetVariable} = CallGetPropertyOffsetFromName({nativeType}, \"{SourceName}\");");
        }

        if (SetterMethod.HasCustomPropertyMethod())
        {
            SetterMethod!.Value.CustomPropertyMethod!.ExportBackingVariablesToStaticConstructor(builder, nativeType);
        }

        if (GetterMethod.HasCustomPropertyMethod())
        {
            GetterMethod!.Value.CustomPropertyMethod!.ExportBackingVariablesToStaticConstructor(builder, nativeType);
        }
    }

    public void ExportNativeProperty(GeneratorStringBuilder builder)
    {
        builder.AppendNewBackingField($"static IntPtr {NativePropertyVariable};");
    }
    
    protected string AppendOffsetMath(string basePtr)
    {
        return $"{basePtr} + {OffsetVariable}";
    }

    public virtual void ExportToNative(GeneratorStringBuilder builder, string buffer, string value)
    {
        AppendCallToNative(builder, MarshallerType, buffer, value);
    }
    
    public virtual void ExportFromNative(GeneratorStringBuilder builder, string buffer, string? assignmentOperator = null)
    {
        AppendCallFromNative(builder, MarshallerType, buffer, assignmentOperator);
    }
    
    protected void AppendCallToNative(GeneratorStringBuilder builder, string marshaller, string buffer, string value)
    {
        string offsetMathOperation = PropertyFlags.IsReturnValue() ? buffer : AppendOffsetMath(buffer);
        builder.Append($"{marshaller}.ToNative({offsetMathOperation}, 0, {value});");
    }
    
    protected void AppendCallFromNative(GeneratorStringBuilder builder, string marshaller, string buffer, string? assignmentOperator = null)
    {
        builder.Append($"{assignmentOperator}{marshaller}.FromNative({AppendOffsetMath(buffer)}, 0);");
    }

    public override void PopulateJsonObject(JsonWriter jsonWriter)
    {
        base.PopulateJsonObject(jsonWriter);

        jsonWriter.TrySetJsonEnum("PropertyFlags", PropertyFlags);
        jsonWriter.TrySetJsonEnum("PropertyType", PropertyType);
        jsonWriter.TrySetJsonBoolean("DefaultComponent", DefaultComponent);
        jsonWriter.TrySetJsonBoolean("IsRootComponent", IsRootComponent);
        jsonWriter.TrySetJsonString("AttachmentComponent", AttachmentComponent);
        jsonWriter.TrySetJsonString("AttachmentSocket", AttachmentSocket);
        jsonWriter.TrySetJsonString("ReplicatedUsing", ReplicatedUsing);
        jsonWriter.TrySetJsonEnum("LifetimeCondition", LifetimeCondition);
        
        // Output custom getter/setter methods
        SetGetterSetterToJson(jsonWriter, "GetterMethod", GetterMethod);
        SetGetterSetterToJson(jsonWriter, "SetterMethod", SetterMethod);
        
        // Output PropertyHook functions (if no custom getter/setter)
        if (!GetterMethod.HasCustomPropertyMethod() && _hookGetterFunction != null)
        {
            SetHookFunctionToJson(jsonWriter, "GetterMethod", _hookGetterFunction);
        }
        if (!SetterMethod.HasCustomPropertyMethod() && _hookSetterFunction != null)
        {
            SetHookFunctionToJson(jsonWriter, "SetterMethod", _hookSetterFunction);
        }
    }
    
    private void SetGetterSetterToJson(JsonWriter jsonWriter, string key, PropertyMethod? method)
    {
        if (method == null || !method.Value.HasCustomMethod)
        {
            return;
        }

        jsonWriter.WritePropertyName(key);
        jsonWriter.WriteStartObject();
        method.Value.CustomPropertyMethod!.PopulateJsonObject(jsonWriter);
        jsonWriter.WriteEndObject();
    }
    
    private void SetHookFunctionToJson(JsonWriter jsonWriter, string key, UnrealPropertyHookFunction hookFunction)
    {
        jsonWriter.WritePropertyName(key);
        jsonWriter.WriteStartObject();
        hookFunction.PopulateJsonObject(jsonWriter);
        jsonWriter.WriteEndObject();
    }
}