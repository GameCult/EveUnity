using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Eve.Surface
{
    /// <summary>
    /// Fluent CultUI/Eve surface builder. Eve owns the surface document; CultMesh only supplies typed binding descriptors.
    /// </summary>
    public sealed class EveSurfaceBuilder
    {
        private readonly string _surfaceId;
        private readonly List<EveSurfaceComponent> _children = new();
        private readonly List<EveStyleToken> _styles = new();
        private readonly List<EveCommandTemplate> _commands = new();
        private EveSurfaceComponent? _root;
        private long _version = 1;
        private string _providerId = "gamecult";
        private string _providerKind = "cultui.surface";
        private string _title = "";
        private string _updatedAtUtc = "";

        public EveSurfaceBuilder(string surfaceId)
        {
            _surfaceId = RequireNonEmpty(surfaceId, nameof(surfaceId));
        }

        public EveSurfaceBuilder Provider(string providerId, string providerKind)
        {
            _providerId = providerId ?? "";
            _providerKind = providerKind ?? "";
            return this;
        }

        public EveSurfaceBuilder Title(string title)
        {
            _title = title ?? "";
            _children.Add(Text($"{_surfaceId}.title", _title, "text.title"));
            return this;
        }

        public EveSurfaceBuilder TitleSubtitle(string title, string subtitle)
        {
            _title = string.IsNullOrWhiteSpace(subtitle) ? title ?? "" : $"{title} {subtitle}";
            _children.Add(Text($"{_surfaceId}.title", title ?? "", "text.title"));
            _children.Add(Text($"{_surfaceId}.subtitle", subtitle ?? "", "text.subtitle"));
            return this;
        }

        public EveSurfaceBuilder Text(string text, string? id = null)
        {
            _children.Add(Text(id ?? $"{_surfaceId}.text.{_children.Count}", text, "text"));
            return this;
        }

        public EveSurfaceBuilder Button(string label, string command)
        {
            var operation = CultMesh.OperationBinding(command, label);
            _commands.Add(new EveCommandTemplate(operation));
            _children.Add(ButtonComponent($"{_surfaceId}.button.{Slug(label)}", label, operation));
            return this;
        }

        public EveSurfaceBuilder Button(string label, CultMeshOperationBindingDescriptor operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _commands.Add(new EveCommandTemplate(operation));
            _children.Add(ButtonComponent($"{_surfaceId}.button.{Slug(label)}", label, operation));
            return this;
        }

        public EveSurfaceBuilder ButtonColumn(string id, Action<EveSurfaceGroupBuilder> build)
        {
            return Group(id, "column", build);
        }

        public EveSurfaceBuilder ButtonRow(string id, Action<EveSurfaceGroupBuilder> build)
        {
            return Group(id, "row", build);
        }

        public EveSurfaceBuilder Form(string id, Action<EveSurfaceFormBuilder> build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            var form = new EveSurfaceFormBuilder(id, AddCommand);
            build(form);
            _children.Add(form.Build());
            return this;
        }

        public EveSurfaceBuilder RootColumn(string id, Action<EveSurfaceContainerBuilder> build)
        {
            _root = BuildContainer(id, "partition", Props(("split", "y")), build);
            return this;
        }

        public EveSurfaceBuilder RootRow(string id, Action<EveSurfaceContainerBuilder> build)
        {
            _root = BuildContainer(id, "partition", Props(("split", "x")), build);
            return this;
        }

        public EveSurfaceBuilder RootGrid(
            string id,
            int columns,
            int rows,
            Action<EveSurfaceContainerBuilder> build)
        {
            _root = BuildContainer(
                id,
                "grid",
                Props(
                    ("columns", columns.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("rows", rows.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                build);
            return this;
        }

        public EveSurfaceBuilder Row(string id, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(id, "partition", Props(("split", "x")), build));
            return this;
        }

        public EveSurfaceBuilder Column(string id, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(id, "partition", Props(("split", "y")), build));
            return this;
        }

        public EveSurfaceBuilder Grid(string id, int columns, int rows, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(
                id,
                "grid",
                Props(
                    ("columns", columns.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("rows", rows.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                build));
            return this;
        }

        public EveSurfaceBuilder Modal(string id, string title, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(id, "modal", Props(("title", title ?? "")), build));
            return this;
        }

        public EveSurfaceBuilder EmbeddedDocument(
            string slotId,
            string documentId,
            string schemaId,
            string presentationKind,
            CultMeshRouteHint? routeHint = null)
        {
            _children.Add(new EveSurfaceComponent(
                $"{_surfaceId}.slot.{Slug(slotId)}",
                "surface.slot",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["slotId"] = slotId ?? "",
                    ["documentId"] = documentId ?? "",
                    ["schemaId"] = schemaId ?? "",
                    ["presentationKind"] = presentationKind ?? ""
                },
                Array.Empty<EveSurfaceComponent>(),
                Array.Empty<CultMeshStateBindingDescriptor>(),
                new[]
                {
                    new EveEmbeddedDocumentSlot(
                        slotId ?? "",
                        documentId ?? "",
                        schemaId ?? "",
                        presentationKind ?? "",
                        routeHint)
                }));
            return this;
        }

        public EveSurfaceBuilder Style(string name, string value)
        {
            _styles.Add(new EveStyleToken(name, value));
            return this;
        }

        public EveSurfaceBuilder Version(long version)
        {
            _version = version;
            return this;
        }

        public EveSurfaceBuilder UpdatedAtUtc(string updatedAtUtc)
        {
            _updatedAtUtc = updatedAtUtc ?? "";
            return this;
        }

        public EveSurfaceDocument Build()
        {
            return new EveSurfaceDocument(
                _providerId,
                _providerKind,
                _title,
                _version,
                string.IsNullOrWhiteSpace(_updatedAtUtc) ? DateTime.UtcNow.ToString("O") : _updatedAtUtc,
                new EveSurfaceTree(
                    _surfaceId,
                    _root ?? new EveSurfaceComponent(
                        $"{_surfaceId}.root",
                        "surface",
                        EmptyProps(),
                        _children.ToArray()),
                    _styles.ToArray()),
                _commands.ToArray());
        }

        private EveSurfaceBuilder Group(string id, string kind, Action<EveSurfaceGroupBuilder> build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            var group = new EveSurfaceGroupBuilder(id, kind, AddCommand);
            build(group);
            _children.Add(group.Build());
            return this;
        }

        private EveSurfaceComponent BuildContainer(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            Action<EveSurfaceContainerBuilder> build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            var container = new EveSurfaceContainerBuilder(id, kind, props, AddCommand);
            build(container);
            return container.Build();
        }

        private void AddCommand(EveCommandTemplate command)
        {
            if (command != null)
                _commands.Add(command);
        }

        internal static EveSurfaceComponent Text(string id, string value, string kind)
        {
            return new EveSurfaceComponent(
                id,
                kind,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["value"] = value ?? ""
                },
                Array.Empty<EveSurfaceComponent>());
        }

        internal static EveSurfaceComponent ButtonComponent(
            string id,
            string label,
            CultMeshOperationBindingDescriptor operation)
        {
            return new EveSurfaceComponent(
                id,
                "control.button",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["label"] = label ?? operation.Label,
                    ["command"] = operation.OperationId,
                    ["operationId"] = operation.OperationId,
                    ["schemaId"] = operation.SchemaId
                },
                Array.Empty<EveSurfaceComponent>());
        }

        internal static Dictionary<string, string> EmptyProps()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        internal static Dictionary<string, string> Props(params (string Key, string Value)[] values)
        {
            return (values ?? Array.Empty<(string Key, string Value)>())
                .ToDictionary(value => value.Key, value => value.Value ?? "", StringComparer.Ordinal);
        }

        internal static string Slug(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "unnamed";

            var chars = value
                .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '.')
                .ToArray();
            var slug = new string(chars).Trim('.');
            while (slug.Contains("..", StringComparison.Ordinal))
                slug = slug.Replace("..", ".", StringComparison.Ordinal);
            return string.IsNullOrWhiteSpace(slug) ? "unnamed" : slug;
        }

        private static string RequireNonEmpty(string value, string paramName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Value must be non-empty.", paramName)
                : value;
        }
    }

    public sealed class EveSurfaceContainerBuilder
    {
        private readonly string _id;
        private readonly string _kind;
        private readonly Dictionary<string, string> _props;
        private readonly Action<EveCommandTemplate> _addCommand;
        private readonly List<EveSurfaceComponent> _children = new();
        private readonly List<CultMeshStateBindingDescriptor> _stateBindings = new();
        private readonly List<EveEmbeddedDocumentSlot> _embeddedDocuments = new();

        internal EveSurfaceContainerBuilder(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            Action<EveCommandTemplate> addCommand)
        {
            _id = id ?? "";
            _kind = kind ?? "partition";
            _props = new Dictionary<string, string>(props ?? EveSurfaceBuilder.EmptyProps(), StringComparer.Ordinal);
            _addCommand = addCommand ?? throw new ArgumentNullException(nameof(addCommand));
        }

        public EveSurfaceContainerBuilder Gap(int value) => Prop("gap", value);

        public EveSurfaceContainerBuilder Padding(int value) => Prop("padding", value);

        public EveSurfaceContainerBuilder Size(string value) => Prop("size", value);

        public EveSurfaceContainerBuilder Min(string value) => Prop("min", value);

        public EveSurfaceContainerBuilder Max(string value) => Prop("max", value);

        public EveSurfaceContainerBuilder Align(string value) => Prop("align", value);

        public EveSurfaceContainerBuilder Clip(string value = "hidden") => Prop("clip", value);

        public EveSurfaceContainerBuilder Scroll(string value = "y") => Prop("scroll", value);

        public EveSurfaceContainerBuilder Region(string value) => Prop("region", value);

        public EveSurfaceContainerBuilder Bind(CultMeshStateBindingDescriptor binding)
        {
            if (binding != null)
                _stateBindings.Add(binding);
            return this;
        }

        public EveSurfaceContainerBuilder Row(string id, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(id, "partition", EveSurfaceBuilder.Props(("split", "x")), build));
            return this;
        }

        public EveSurfaceContainerBuilder Column(string id, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(id, "partition", EveSurfaceBuilder.Props(("split", "y")), build));
            return this;
        }

        public EveSurfaceContainerBuilder Grid(
            string id,
            int columns,
            int rows,
            Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(
                id,
                "grid",
                EveSurfaceBuilder.Props(
                    ("columns", columns.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("rows", rows.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                build));
            return this;
        }

        public EveSurfaceContainerBuilder Pane(string id, string title, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(id, "pane", EveSurfaceBuilder.Props(("title", title ?? "")), build));
            return this;
        }

        public EveSurfaceContainerBuilder Card(string id, string title, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(id, "card", EveSurfaceBuilder.Props(("title", title ?? "")), build));
            return this;
        }

        public EveSurfaceContainerBuilder Modal(string id, string title, Action<EveSurfaceContainerBuilder> build)
        {
            _children.Add(BuildContainer(id, "modal", EveSurfaceBuilder.Props(("title", title ?? "")), build));
            return this;
        }

        public EveSurfaceContainerBuilder FieldRow(
            string id,
            string label,
            Action<EveSurfaceContainerBuilder> build)
        {
            return Row(id, row => row
                .Gap(8)
                .Label($"{id}.label", label, labelPart => labelPart.Size("12rem"))
                .Column($"{id}.field", field =>
                {
                    field.Size("1fr");
                    build(field);
                }));
        }

        public EveSurfaceContainerBuilder Label(
            string id,
            string text,
            Action<EveSurfaceContainerBuilder>? configure = null)
        {
            return Leaf(id, "label", EveSurfaceBuilder.Props(("value", text ?? "")), null, null, configure);
        }

        public EveSurfaceContainerBuilder Text(string id, string text)
        {
            return Leaf(id, "text", EveSurfaceBuilder.Props(("value", text ?? "")));
        }

        public EveSurfaceContainerBuilder Title(string id, string text)
        {
            return Leaf(id, "text.title", EveSurfaceBuilder.Props(("value", text ?? "")));
        }

        public EveSurfaceContainerBuilder Metric(
            string id,
            string label,
            string value,
            CultMeshStateBindingDescriptor? binding = null)
        {
            return Leaf(
                id,
                "metric",
                EveSurfaceBuilder.Props(("label", label ?? ""), ("value", value ?? "")),
                binding);
        }

        public EveSurfaceContainerBuilder Progress(
            string id,
            string label,
            string value,
            string min,
            string max,
            CultMeshStateBindingDescriptor? binding = null)
        {
            return Leaf(
                id,
                "progress",
                EveSurfaceBuilder.Props(
                    ("label", label ?? ""),
                    ("value", value ?? ""),
                    ("min", min ?? ""),
                    ("max", max ?? "")),
                binding);
        }

        public EveSurfaceContainerBuilder Button(string id, string label, string command)
        {
            return Button(id, label, CultMesh.OperationBinding(command, label));
        }

        public EveSurfaceContainerBuilder Button(
            string id,
            string label,
            CultMeshOperationBindingDescriptor operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveCommandTemplate(operation));
            _children.Add(EveSurfaceBuilder.ButtonComponent(id, label, operation));
            return this;
        }

        public EveSurfaceContainerBuilder PopupButton(
            string id,
            string label,
            string title,
            Action<EveSurfaceContainerBuilder> build,
            string anchor = "viewport",
            string placement = "center",
            int offsetX = 0,
            int offsetY = 0)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            var popup = BuildContainer(
                $"{id}.popup",
                "modal",
                EveSurfaceBuilder.Props(
                    ("title", title ?? label ?? ""),
                    ("anchor", anchor ?? "viewport"),
                    ("placement", placement ?? "center"),
                    ("offsetX", offsetX.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("offsetY", offsetY.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("presentation", "popover")),
                build);
            _children.Add(new EveSurfaceComponent(
                id ?? "",
                "control.popup",
                EveSurfaceBuilder.Props(
                    ("label", label ?? ""),
                    ("title", title ?? label ?? ""),
                    ("anchor", anchor ?? "viewport"),
                    ("placement", placement ?? "center"),
                    ("offsetX", offsetX.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("offsetY", offsetY.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                new[] { popup }));
            return this;
        }

        public EveSurfaceContainerBuilder EnumField(
            string id,
            string label,
            string value,
            CultMeshStateBindingDescriptor optionsBinding,
            CultMeshOperationBindingDescriptor setValue,
            string anchor = "trigger",
            string placement = "right-down")
        {
            return FieldRow(id, label, field => field.PopupButton(
                $"{id}.dropdown",
                value,
                label,
                popup => popup.OptionList($"{id}.options", label, optionsBinding, setValue),
                anchor,
                placement));
        }

        public EveSurfaceContainerBuilder OptionList(
            string id,
            string label,
            CultMeshStateBindingDescriptor itemsBinding,
            CultMeshOperationBindingDescriptor operation)
        {
            if (itemsBinding == null) throw new ArgumentNullException(nameof(itemsBinding));
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveCommandTemplate(operation));
            _children.Add(new EveSurfaceComponent(
                id ?? "",
                "options",
                EveSurfaceBuilder.Props(
                    ("label", label ?? ""),
                    ("command", operation.OperationId),
                    ("operationId", operation.OperationId),
                    ("schemaId", operation.SchemaId)),
                Array.Empty<EveSurfaceComponent>(),
                new[] { itemsBinding }));
            return this;
        }

        public EveSurfaceContainerBuilder TextInput(
            string id,
            string label,
            string value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding = null)
        {
            return Control("control.input.text", id, label, value, operation, binding);
        }

        public EveSurfaceContainerBuilder Toggle(
            string id,
            string label,
            bool value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding = null)
        {
            return Control("control.toggle", id, label, value ? "true" : "false", operation, binding);
        }

        public EveSurfaceContainerBuilder Slider(
            string id,
            string label,
            string value,
            string min,
            string max,
            string step,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding = null)
        {
            return Control(
                "control.slider",
                id,
                label,
                value,
                operation,
                binding,
                ("min", min ?? ""),
                ("max", max ?? ""),
                ("step", step ?? ""));
        }

        public EveSurfaceContainerBuilder Select(
            string id,
            string label,
            string value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding = null)
        {
            return Control("control.select", id, label, value, operation, binding);
        }

        public EveSurfaceContainerBuilder EmbeddedDocument(
            string id,
            string slotId,
            string documentId,
            string schemaId,
            string presentationKind,
            CultMeshRouteHint? routeHint = null)
        {
            _children.Add(new EveSurfaceComponent(
                id ?? "",
                "surface.slot",
                EveSurfaceBuilder.Props(
                    ("slotId", slotId ?? ""),
                    ("documentId", documentId ?? ""),
                    ("schemaId", schemaId ?? ""),
                    ("presentationKind", presentationKind ?? "")),
                Array.Empty<EveSurfaceComponent>(),
                Array.Empty<CultMeshStateBindingDescriptor>(),
                new[]
                {
                    new EveEmbeddedDocumentSlot(
                        slotId ?? "",
                        documentId ?? "",
                        schemaId ?? "",
                        presentationKind ?? "",
                        routeHint)
                }));
            return this;
        }

        internal EveSurfaceComponent Build()
        {
            return new EveSurfaceComponent(
                _id,
                _kind,
                _props,
                _children.ToArray(),
                _stateBindings.ToArray(),
                _embeddedDocuments.ToArray());
        }

        private EveSurfaceContainerBuilder Prop(string key, int value)
        {
            _props[key] = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return this;
        }

        private EveSurfaceContainerBuilder Prop(string key, string value)
        {
            _props[key] = value ?? "";
            return this;
        }

        private EveSurfaceContainerBuilder Leaf(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            CultMeshStateBindingDescriptor? binding = null,
            IReadOnlyList<EveSurfaceComponent>? children = null,
            Action<EveSurfaceContainerBuilder>? configure = null)
        {
            var builder = new EveSurfaceContainerBuilder(id, kind, props, _addCommand);
            configure?.Invoke(builder);
            var component = new EveSurfaceComponent(
                id ?? "",
                kind ?? "",
                new Dictionary<string, string>(props ?? EveSurfaceBuilder.EmptyProps(), StringComparer.Ordinal),
                children ?? Array.Empty<EveSurfaceComponent>(),
                binding == null ? Array.Empty<CultMeshStateBindingDescriptor>() : new[] { binding });
            if (builder._props.Count != (props?.Count ?? 0))
            {
                component = new EveSurfaceComponent(
                    id ?? "",
                    kind ?? "",
                    new Dictionary<string, string>(builder._props, StringComparer.Ordinal),
                    children ?? Array.Empty<EveSurfaceComponent>(),
                    binding == null ? Array.Empty<CultMeshStateBindingDescriptor>() : new[] { binding });
            }
            _children.Add(component);
            return this;
        }

        private EveSurfaceContainerBuilder Control(
            string kind,
            string id,
            string label,
            string value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding,
            params (string Key, string Value)[] extraProps)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveCommandTemplate(operation));
            var props = EveSurfaceBuilder.Props(
                new[]
                {
                    ("label", label ?? ""),
                    ("value", value ?? ""),
                    ("command", operation.OperationId),
                    ("operationId", operation.OperationId),
                    ("schemaId", operation.SchemaId)
                }.Concat(extraProps ?? Array.Empty<(string Key, string Value)>()).ToArray());
            return Leaf(kind: kind, id: id, props: props, binding: binding);
        }

        private EveSurfaceComponent BuildContainer(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            Action<EveSurfaceContainerBuilder> build)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));
            var container = new EveSurfaceContainerBuilder(id, kind, props, _addCommand);
            build(container);
            return container.Build();
        }
    }

    public sealed class EveSurfaceGroupBuilder
    {
        private readonly string _id;
        private readonly string _kind;
        private readonly Action<EveCommandTemplate> _addCommand;
        private readonly List<EveSurfaceComponent> _children = new();

        internal EveSurfaceGroupBuilder(
            string id,
            string kind,
            Action<EveCommandTemplate> addCommand)
        {
            _id = id ?? "";
            _kind = kind ?? "column";
            _addCommand = addCommand ?? throw new ArgumentNullException(nameof(addCommand));
        }

        public EveSurfaceGroupBuilder Button(string label, string command)
        {
            var operation = CultMesh.OperationBinding(command, label);
            _addCommand(new EveCommandTemplate(operation));
            _children.Add(EveSurfaceBuilder.ButtonComponent($"{_id}.button.{EveSurfaceBuilder.Slug(label)}", label, operation));
            return this;
        }

        public EveSurfaceGroupBuilder Button(string label, CultMeshOperationBindingDescriptor operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveCommandTemplate(operation));
            _children.Add(EveSurfaceBuilder.ButtonComponent($"{_id}.button.{EveSurfaceBuilder.Slug(label)}", label, operation));
            return this;
        }

        internal EveSurfaceComponent Build()
        {
            return new EveSurfaceComponent(_id, _kind, EveSurfaceBuilder.EmptyProps(), _children.ToArray());
        }
    }

    public sealed class EveSurfaceFormBuilder
    {
        private readonly string _id;
        private readonly Action<EveCommandTemplate> _addCommand;
        private readonly List<EveSurfaceComponent> _children = new();

        internal EveSurfaceFormBuilder(string id, Action<EveCommandTemplate> addCommand)
        {
            _id = id ?? "";
            _addCommand = addCommand ?? throw new ArgumentNullException(nameof(addCommand));
        }

        public EveSurfaceFormBuilder Text(
            string label,
            string value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveCommandTemplate(operation));
            _children.Add(Control(
                "control.text",
                label,
                value,
                operation,
                binding));
            return this;
        }

        public EveSurfaceFormBuilder Toggle(
            string label,
            bool value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding = null)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            _addCommand(new EveCommandTemplate(operation));
            _children.Add(Control(
                "control.toggle",
                label,
                value ? "true" : "false",
                operation,
                binding));
            return this;
        }

        public EveSurfaceFormBuilder Metric(
            string label,
            string value,
            CultMeshStateBindingDescriptor? binding = null)
        {
            var bindings = binding == null
                ? Array.Empty<CultMeshStateBindingDescriptor>()
                : new[] { binding };
            _children.Add(new EveSurfaceComponent(
                $"{_id}.metric.{EveSurfaceBuilder.Slug(label)}",
                "metric",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["label"] = label ?? "",
                    ["value"] = value ?? ""
                },
                Array.Empty<EveSurfaceComponent>(),
                bindings));
            return this;
        }

        internal EveSurfaceComponent Build()
        {
            return new EveSurfaceComponent(_id, "form", EveSurfaceBuilder.EmptyProps(), _children.ToArray());
        }

        private EveSurfaceComponent Control(
            string kind,
            string label,
            string value,
            CultMeshOperationBindingDescriptor operation,
            CultMeshStateBindingDescriptor? binding)
        {
            var bindings = binding == null
                ? Array.Empty<CultMeshStateBindingDescriptor>()
                : new[] { binding };
            return new EveSurfaceComponent(
                $"{_id}.{EveSurfaceBuilder.Slug(label)}",
                kind,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["label"] = label ?? "",
                    ["value"] = value ?? "",
                    ["command"] = operation.OperationId,
                    ["operationId"] = operation.OperationId,
                    ["schemaId"] = operation.SchemaId
                },
                Array.Empty<EveSurfaceComponent>(),
                bindings);
        }
    }

    public static class EveSurface
    {
        public static EveSurfaceBuilder Create(string surfaceId)
        {
            return new EveSurfaceBuilder(surfaceId);
        }
    }
}
