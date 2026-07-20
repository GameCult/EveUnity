using System;
using GameCult.Eve.UnityUIToolkit;
using UnityEngine;
using UnityEngine.UIElements;

#nullable enable

namespace GameCult.Eve.UnityScene
{
    public sealed class EveUnityUiToolkitOverlay : MonoBehaviour
    {
        private IEveUnitySceneProviderSurfaceDocumentSource? _documents;
        private IEveUnitySceneCommandSink? _commands;
        private UIDocument? _document;
        private EveUiToolkitSurfacePresenter? _presenter;
        private VisualElement? _mountedRoot;

        public VisualElement? Root => _mountedRoot;

        public void Bind(MonoBehaviour documentSource, MonoBehaviour commandSink)
        {
            if (documentSource is not IEveUnitySceneProviderSurfaceDocumentSource documents)
                throw new ArgumentException(
                    $"{nameof(documentSource)} must implement {nameof(IEveUnitySceneProviderSurfaceDocumentSource)}.",
                    nameof(documentSource));
            if (commandSink is not IEveUnitySceneCommandSink commands)
                throw new ArgumentException(
                    $"{nameof(commandSink)} must implement {nameof(IEveUnitySceneCommandSink)}.",
                    nameof(commandSink));

            Unsubscribe();
            _documents = documents;
            _commands = commands;
            _documents.DocumentAvailable += OnDocumentAvailable;
            EnsureDocument();
            Present(_documents.CurrentDocument);
        }

        private void OnDestroy() => Unsubscribe();

        private void OnDocumentAvailable(EveUnitySceneProviderSurfaceDocument document) => Present(document);

        private void Present(EveUnitySceneProviderSurfaceDocument document)
        {
            if (document == null || _commands == null) return;
            _presenter ??= new EveUiToolkitSurfacePresenter();
            var replaced = _presenter.Present(document.SurfaceDocument, _commands.Submit);
            if (replaced && _presenter.Root != null)
            {
                _mountedRoot?.RemoveFromHierarchy();
                _mountedRoot = _presenter.Root;
                _mountedRoot.AddToClassList("eve-playable-world-ui-overlay");
            }
            AttachToDocument();
        }

        private void EnsureDocument()
        {
            _document = GetComponent<UIDocument>();
            if (_document != null) return;
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.name = "Eve playable-world surface panel";
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1920, 1080);
            panel.match = 0.5f;
            _document = gameObject.AddComponent<UIDocument>();
            _document.panelSettings = panel;
            _document.sortingOrder = 100;
        }

        private void AttachToDocument()
        {
            EnsureDocument();
            var documentRoot = _document?.rootVisualElement;
            if (_mountedRoot != null && _mountedRoot.parent == null && documentRoot != null)
                documentRoot.Add(_mountedRoot);
        }

        private void Unsubscribe()
        {
            if (_documents != null)
                _documents.DocumentAvailable -= OnDocumentAvailable;
            _documents = null;
            _commands = null;
        }
    }
}
