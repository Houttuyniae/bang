﻿using Bang.Components;
using System.Collections.Immutable;
using System.Diagnostics;

namespace Bang.Entities
{
    /// <summary>
    /// This file has all the content for entities supporting children
    /// and parents - a family!
    /// </summary>
    public partial class Entity
    {
        private Entity? _parent;

        /// <summary>
        /// All the children tracked by the entity.
        /// Maps:
        ///   [Child id => Child name]
        /// </summary>
        private IDictionary<int, string?>? _children;

        /// <summary>
        /// All the children tracked by the entity.
        /// Maps:
        ///   [Child name => Child id]
        /// </summary>
        private IDictionary<string, int>? _childrenPerName;

        /// <summary>
        /// This is the unique id of the parent of the entity.
        /// Null if none (no parent).
        /// </summary>
        public int? Parent => _parent?.EntityId;

        private ImmutableArray<int>? _cachedChildren = null;

        /// <summary>
        /// Unique id of all the children of the entity.
        /// </summary>
        public ImmutableArray<int> Children
        {
            get
            {
                if (_cachedChildren is null)
                {
                    _cachedChildren = _children is null ? ImmutableArray<int>.Empty : _children.Keys.ToImmutableArray();
                }

                return _cachedChildren.Value;
            }
        }

        /// <summary>
        /// Fetch a list of all the unique identifiers of the children with their respective names.
        /// </summary>
        public ImmutableDictionary<int, string?> FetchChildrenWithNames =>
            _children is null ? ImmutableDictionary<int, string?>.Empty : _children.ToImmutableDictionary();

        /// <summary>
        /// Try to fetch a child with a <paramref name="name"/> identifier
        /// </summary>
        /// <param name="name">The name of the child.</param>
        /// <returns>Child entity, if any.</returns>
        public Entity? TryFetchChild(string name)
        {
            if (_childrenPerName is null)
            {
                return default;
            }

            if (!_childrenPerName.TryGetValue(name, out int child))
            {
                return default;
            }

            return _world?.TryGetEntity(child);
        }

        /// <summary>
        /// Try to fetch the parent entity.
        /// </summary>
        /// <returns>Parent entity. If none, returns null.</returns>
        public Entity? TryFetchParent()
        {
            if (_world is null || Parent is null || _isDestroyed)
            {
                return null;
            }

            return _world.TryGetEntity(Parent.Value);
        }

        /// <summary>
        /// This fetches a child with a given component.
        /// TODO: Optimize, or cache?
        /// </summary>
        public Entity? TryFetchChildWithComponent<T>() where T : IComponent
        {
            foreach (int childId in Children)
            {
                Entity? child = _world?.TryGetEntity(childId);
                if (child?.HasComponent<T>() ?? false)
                {
                    return child;
                }
            }

            return null;
        }
        
        /// <summary>
        /// Track whenever a component of index <paramref name="index"/> gets modified.
        /// This is used by the entity's children in order to track a component changes.
        /// </summary>
        private void TrackComponent(int index, Action<int, IComponent> notification)
        {
            if (_trackedComponentsModified.ContainsKey(index))
            {
                _trackedComponentsModified[index] += notification;
            }
            else
            {
                _trackedComponentsModified.Add(index, notification);
            }
        }

        /// <summary>
        /// Untracks whenever a component of index <paramref name="index"/> gets modified.
        /// </summary>
        private void UntrackComponent(int index, Action<int, IComponent> notification)
        {
            if (!_trackedComponentsModified.TryGetValue(index, out var notifications) || notifications is null)
            {
                return;
            }

            _trackedComponentsModified[index] -= notification;

            if (_trackedComponentsModified[index] is null)
            {
                _trackedComponentsModified.Remove(index);
            }
        }

        /// <summary>
        /// Assign an existing entity as a child.
        /// </summary>
        /// <param name="id">Id of the entity.</param>
        /// <param name="name">Name of the child (if none, null).</param>
        public void AddChild(int id, string? name = default)
        {
            Debug.Assert(_world is not null);

            if (_children is not null && _children.ContainsKey(id))
            {
                // Child was already added!
                return;
            }

            _children ??= new Dictionary<int, string?>();
            _children.Add(id, name);

            // Bookkeep name!
            if (!string.IsNullOrEmpty(name))
            {
                _childrenPerName ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                Debug.Assert(!_childrenPerName.ContainsKey(name), "Duplicate child name!");
                _childrenPerName.Add(name, id);
            }

            _cachedChildren = null;

            Entity child = _world.GetEntity(id);

            child.OnEntityDestroyed += RemoveChild;
            child.Reparent(this);
        }

        /// <summary>
        /// Try to fetch a child with a <paramref name="name"/> identifier
        /// </summary>
        /// <param name="name">The name of the child.</param>
        /// <returns>Child entity, if any.</returns>
        public void RemoveChild(string name)
        {
            if (_childrenPerName is null)
            {
                return;
            }

            if (!_childrenPerName.TryGetValue(name, out int child))
            {
                return;
            }

            RemoveChild(child);
        }

        /// <summary>
        /// Remove a child from the entity.
        /// </summary>
        public void RemoveChild(int id)
        {
            Debug.Assert(_world is not null);

            if (_children is null)
            {
                return;
            }

            if (IsDestroyed)
            {
                // If the parent has been destroyed, it's likely that this triggered the child code path.
                // Do not remove the child.
                return;
            }

            if (!_children.ContainsKey(id))
            {
                // Child was already removed!
                return;
            }

            // Bookkeep name!
            string? name = _children[id];
            if (!string.IsNullOrEmpty(name))
            {
                _childrenPerName?.Remove(name);
            }

            _children?.Remove(id);

            _cachedChildren = null;

            Entity? child = _world.TryGetEntity(id);
            child?.Unparent();
        }  

        /// <summary>
        /// Set the parent of this entity.
        /// </summary>
        public void Reparent(Entity parent)
        {
            Debug.Assert(_lookup is not null);

            if (parent == _parent)
            {
                // Parent is already the same!
                return;
            }

            if (parent.IsDestroyed)
            {
                // New parent is dead! Immediate suicide.
                Destroy();
                return;
            }

            Unparent();

            if (parent is null)
            {
                // Dismiss any notifications.
                return;
            }

            _parent = parent;

            foreach (int index in _lookup.RelativeComponents)
            {
                if (HasComponent(index))
                {
                    _parent.TrackComponent(index, OnParentModified);

                    if (_parent.TryGetComponent(index, out IComponent? parentComponent))
                    {
                        OnParentModified(index, parentComponent);
                    }
                }
            }

            _parent.OnEntityDestroyed += Destroy;
            _parent.AddChild(EntityId);
        }

        /// <summary>
        /// This will remove a parent of the entity.
        /// It untracks all the tracked components and removes itself from the parent's children.
        /// </summary>
        public void Unparent()
        {
            Debug.Assert(_lookup is not null);

            if (_parent is null) return;

            foreach (int index in _lookup.RelativeComponents)
            {
                if (_parent._trackedComponentsModified.ContainsKey(index))
                {
                    _parent._trackedComponentsModified[index] -= OnParentModified;
                }
            }

            _parent.OnEntityDestroyed -= Destroy;
            _parent.RemoveChild(EntityId);

            _parent = null;
        } 

        private void OnParentModified(int index, IComponent c)
        {
            Debug.Assert(index == GetComponentIndex(c.GetType()));

            IParentRelativeComponent? relativeComponent = _components[index] as IParentRelativeComponent;

            Debug.Assert(relativeComponent is not null, "How is one of the relative components null?");
            relativeComponent?.OnParentModified(c, this);
        }
    }
}
