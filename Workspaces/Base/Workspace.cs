﻿using System;
using System.Collections;
using UnityEngine.VR.Handles;
using UnityEngine.VR.Tools;
using UnityEngine.VR.Utilities;
using UnityEngine.VR.Extensions;

namespace UnityEngine.VR.Workspaces
{
	public abstract class Workspace : MonoBehaviour, IInstantiateUI, IHighlight
	{
		public static readonly Vector3 kDefaultBounds = new Vector3(0.7f, 0.4f, 0.4f);
		public static readonly Vector3 kDefaultOffset = new Vector3(0, -0.15f, 1f);
		public static readonly Vector3 kVacuumOffset = new Vector3(0, -0.15f, 0.6f);
		public static readonly Quaternion kDefaultTilt = Quaternion.AngleAxis(-20, Vector3.right);

		public const float kHandleMargin = -0.15f; // Compensate for base size from frame model

		public event Action<Workspace> destroyed = delegate { };

		protected WorkspaceUI m_WorkspaceUI;

		protected Vector3? m_CustomStartingBounds;

		public static readonly Vector3 kMinBounds = new Vector3(0.55f, 0.4f, 0.1f);
		private const float kExtraHeight = 0.15f; //Extra space for frame model

		public Vector3 minBounds { get { return m_MinBounds; } set { m_MinBounds = value; } }
		[SerializeField]
		private Vector3 m_MinBounds = kMinBounds;

		/// <summary>
		/// Bounding box for workspace content (ignores value.center) 
		/// </summary>
		public Bounds contentBounds
		{
			get { return m_ContentBounds; }
			set
			{
				if (!value.Equals(contentBounds))
				{
					Vector3 size = value.size;
					size.x = Mathf.Max(size.x, minBounds.x);
					size.y = Mathf.Max(size.y, minBounds.y);
					size.z = Mathf.Max(size.z, minBounds.z);

					m_ContentBounds.size = size; //Only set size, ignore center.
					UpdateBounds();
					OnBoundsChanged();
				}
			}
		}
		private Bounds m_ContentBounds;

		[SerializeField]
		private GameObject m_BasePrefab;

		private Vector3 m_DragStart;
		private Vector3 m_PositionStart;
		private Vector3 m_BoundSizeStart;
		private bool m_Dragging;
		private bool m_DragLocked;
		private bool m_Vacuuming;
		bool m_Moving;
		Coroutine m_VisibilityCoroutine;
		Coroutine m_ResetSizeCoroutine;
		public bool m_Hidden;

		/// <summary>
		/// Bounding box for entire workspace, including UI handles
		/// </summary>
		public Bounds outerBounds
		{
			get
			{
				return new Bounds(contentBounds.center + Vector3.down * kExtraHeight * 0.5f,
					new Vector3(
						contentBounds.size.x,
						contentBounds.size.y + kExtraHeight,
						contentBounds.size.z
						));
			}
		}

		public Func<GameObject, GameObject> instantiateUI { protected get; set; }

		public Action<GameObject, bool> setHighlight { protected get; set; }

		/// <summary>
		/// If true, allow the front face of the workspace to dynamically adjust its angle when rotated
		/// </summary>
		public bool dynamicFaceAdjustment { set { m_WorkspaceUI.dynamicFaceAdjustment = value; } }

		/// <summary>
		/// If true, prevent the resizing of a workspace via the front and back resize handles
		/// </summary>
		public bool preventFrontBackResize { set { m_WorkspaceUI.preventFrontBackResize = value; } }

		/// <summary>
		/// If true, prevent the resizing of a workspace via the left and right resize handles
		/// </summary>
		public bool preventLeftRightResize { set { m_WorkspaceUI.preventLeftRightResize = value; } }

		/// <summary>
		/// (-1 to 1) ranged value that controls the separator mask's X-offset placement
		/// A value of zero will leave the mask in the center of the workspace
		/// </summary>
		public float topPanelDividerOffset
		{
			set
			{
				m_WorkspaceUI.topPanelDividerOffset = value;
				m_WorkspaceUI.bounds = contentBounds;
			}
		}

		public bool vacuumEnabled { set { m_WorkspaceUI.vacuumHandle.gameObject.SetActive(value); } }

		public virtual void Setup()
		{
			GameObject baseObject = instantiateUI(m_BasePrefab);
			baseObject.transform.SetParent(transform, false);

			m_WorkspaceUI = baseObject.GetComponent<WorkspaceUI>();
			m_WorkspaceUI.closeClicked += OnCloseClicked;
			m_WorkspaceUI.lockClicked += OnLockClicked;
			m_WorkspaceUI.resetSizeClicked += OnResetClicked;

			m_WorkspaceUI.sceneContainer.transform.localPosition = Vector3.zero;

			//Do not set bounds directly, in case OnBoundsChanged requires Setup override to complete
			m_ContentBounds = new Bounds(Vector3.up * kDefaultBounds.y * 0.5f, m_CustomStartingBounds == null ? kDefaultBounds : m_CustomStartingBounds.Value); // If custom bounds have been set, use them as the initial bounds
			UpdateBounds();

			//Set up DirectManipulator
			var directManipulator = m_WorkspaceUI.directManipulator;
			directManipulator.target = transform;
			directManipulator.translate = Translate;
			directManipulator.rotate = Rotate;

			//Set up the front "move" handle highglight, the move handle is used to translate/rotate the workspace
			var moveHandle = m_WorkspaceUI.moveHandle;
			moveHandle.dragStarted += OnMoveHandleDragStarted;
			moveHandle.dragEnded += OnMoveHandleDragEnded;
			moveHandle.hoverStarted += OnMoveHandleHoverStarted;
			moveHandle.hoverEnded += OnMoveHandleHoverEnded;

			m_WorkspaceUI.vacuumHandle.doubleClick += OnDoubleClick;
			m_WorkspaceUI.vacuumHandle.hoverStarted += OnHandleHoverStarted;
			m_WorkspaceUI.vacuumHandle.hoverEnded += OnHandleHoverEnded;

			var handles = new BaseHandle[]
			{
				m_WorkspaceUI.leftHandle,
				m_WorkspaceUI.frontHandle,
				m_WorkspaceUI.backHandle,
				m_WorkspaceUI.rightHandle
			};

			foreach (var handle in handles)
			{
				handle.dragStarted += OnHandleDragStarted;
				handle.dragging += OnHandleDragging;
				handle.dragEnded += OnHandleDragEnded;
				handle.hoverStarted += OnHandleHoverStarted;
				handle.hoverEnded += OnHandleHoverEnded;
			}

			this.StopCoroutine(ref m_VisibilityCoroutine);

			m_VisibilityCoroutine = StartCoroutine(AnimateShow());
		}

		public virtual void OnHandleDragStarted(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			m_WorkspaceUI.highlightsVisible = true;
			m_PositionStart = transform.position;
			m_DragStart = eventData.rayOrigin.position;
			m_BoundSizeStart = contentBounds.size;
			m_Dragging = true;
		}

		public virtual void OnHandleDragging(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			if (m_Dragging && !m_DragLocked)
			{
				Vector3 dragVector = eventData.rayOrigin.position - m_DragStart;
				Bounds bounds = contentBounds;
				Vector3 positionOffset = Vector3.zero;
				if (handle.Equals(m_WorkspaceUI.leftHandle))
				{
					bounds.size = m_BoundSizeStart + Vector3.left * Vector3.Dot(dragVector, transform.right);
					positionOffset = transform.right * Vector3.Dot(dragVector, transform.right) * 0.5f;
				}
				if (handle.Equals(m_WorkspaceUI.frontHandle))
				{
					bounds.size = m_BoundSizeStart + Vector3.back * Vector3.Dot(dragVector, transform.forward);
					positionOffset = transform.forward * Vector3.Dot(dragVector, transform.forward) * 0.5f;
				}
				if (handle.Equals(m_WorkspaceUI.rightHandle))
				{
					bounds.size = m_BoundSizeStart + Vector3.right * Vector3.Dot(dragVector, transform.right);
					positionOffset = transform.right * Vector3.Dot(dragVector, transform.right) * 0.5f;
				}
				if (handle.Equals(m_WorkspaceUI.backHandle))
				{
					bounds.size = m_BoundSizeStart + Vector3.forward * Vector3.Dot(dragVector, transform.forward);
					positionOffset = transform.forward * Vector3.Dot(dragVector, transform.forward) * 0.5f;
				}
				contentBounds = bounds;
				if (contentBounds.size == bounds.size) //Don't reposition if we hit minimum bounds
					transform.position = m_PositionStart + positionOffset;
			}
		}

		public virtual void OnHandleDragEnded(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			m_WorkspaceUI.highlightsVisible = false;
			m_Dragging = false;
		}

		public virtual void OnHandleHoverStarted(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			// TODO: Add new highlight visuals support
			//if (handle == m_WorkspaceUI.vacuumHandle || !m_DragLocked)
			//	setHighlight(handle.gameObject, true);
		}

		public virtual void OnHandleHoverEnded(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			// TODO: Add new highlight visuals support
			//if (handle == m_WorkspaceUI.vacuumHandle || !m_DragLocked)
			//	setHighlight(handle.gameObject, false);
		}

		private void OnDoubleClick(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			if (!m_Vacuuming)
				StartCoroutine(VacuumToViewer());
		}

		public void OnDoubleTriggerTapAboveHMD()
		{
			if(m_Hidden)
			{
				MonoBehaviourExtensions.StopCoroutine(this,ref m_VisibilityCoroutine);
				m_VisibilityCoroutine = StartCoroutine(AnimateShow());
				m_Hidden = false;
			}
		}

		private void Translate(Vector3 deltaPosition)
		{
			if (m_DragLocked)
				return;

			transform.position += deltaPosition;
		}

		private void Rotate(Quaternion deltaRotation)
		{
			if (m_DragLocked)
				return;

			transform.rotation *= deltaRotation;
		}

		private IEnumerator VacuumToViewer()
		{
			m_Vacuuming = true;
			var startPosition = transform.position;
			var startRotation = transform.rotation;

			var camera = U.Camera.GetMainCamera().transform;
			var cameraYawVector = camera.forward;
			cameraYawVector.y = 0;
			var cameraYaw = Quaternion.LookRotation(cameraYawVector, Vector3.up);

			var destPosition = camera.position + cameraYaw * kVacuumOffset;
			var destRotation = cameraYaw * kDefaultTilt;
			var currentValue = 0f;
			var currentVelocity = 0f;
			var currentDuration = 0f;
			const float kTargetValue = 1f;
			const float kTargetDuration = 0.5f;
			while (currentDuration < kTargetDuration)
			{
				currentDuration += Time.unscaledDeltaTime;
				currentValue = U.Math.SmoothDamp(currentValue, kTargetValue, ref currentVelocity, kTargetDuration, Mathf.Infinity, Time.unscaledDeltaTime);
				transform.position = Vector3.Lerp(startPosition, destPosition, currentValue);
				transform.rotation = Quaternion.Lerp(startRotation, destRotation, currentValue);
				yield return null;
			}

			m_Vacuuming = false;
		}

		public virtual void OnCloseClicked()
		{
			if(!m_Hidden)
			{
				this.StopCoroutine(ref m_VisibilityCoroutine);
				m_VisibilityCoroutine = StartCoroutine(AnimateHide());
				m_Hidden = true;
			}
		}

		public virtual void OnLockClicked()
		{
			m_DragLocked = !m_DragLocked;
		}

		public virtual void OnResetClicked()
		{
			m_DragLocked = false;

			this.StopCoroutine(ref m_ResetSizeCoroutine);

			m_ResetSizeCoroutine = StartCoroutine(AnimateResetSize());
		}

		private void UpdateBounds()
		{
			m_WorkspaceUI.vacuumHandle.transform.localPosition = outerBounds.center;
			m_WorkspaceUI.vacuumHandle.transform.localScale = outerBounds.size;
			m_WorkspaceUI.bounds = contentBounds;
		}

		protected virtual void OnDestroy()
		{
			destroyed(this);
		}

		protected virtual void OnBoundsChanged()
		{
		}

		IEnumerator AnimateShow()
		{
			m_WorkspaceUI.highlightsVisible = true;

			var targetScale = Vector3.one;
			var scale = Vector3.zero;
			var smoothVelocity = Vector3.zero;
			var currentDuration = 0f;
			const float kTargetDuration = 0.75f;
			while (currentDuration < kTargetDuration)
			{
				currentDuration += Time.unscaledDeltaTime;
				transform.localScale = scale;
				scale = U.Math.SmoothDamp(scale, targetScale, ref smoothVelocity, kTargetDuration, Mathf.Infinity, Time.unscaledDeltaTime);
				yield return null;
			}
			transform.localScale = targetScale;

			m_WorkspaceUI.highlightsVisible = false;
			m_VisibilityCoroutine = null;
			m_Hidden = false;
		}

		IEnumerator AnimateHide()
		{
			var targetScale = Vector3.zero;
			var scale = transform.localScale;
			var smoothVelocity = Vector3.zero;
			var currentDuration = 0f;
			const float kTargetDuration = 0.185f;
			while (currentDuration < kTargetDuration)
			{
				currentDuration += Time.unscaledDeltaTime;
				transform.localScale = scale;
				scale = U.Math.SmoothDamp(scale, targetScale, ref smoothVelocity, kTargetDuration, Mathf.Infinity, Time.unscaledDeltaTime);
				yield return null;
			}
			transform.localScale = targetScale;

			m_WorkspaceUI.highlightsVisible = false;
			m_VisibilityCoroutine = null;
			m_Hidden = true;
		}

		void OnMoveHandleDragStarted(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			if (m_Dragging)
				return;

			m_Moving = true;
			m_WorkspaceUI.highlightsVisible = true;
		}

		void OnMoveHandleDragEnded(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			if (m_Dragging)
				return;

			m_Moving = false;
			m_WorkspaceUI.highlightsVisible = false;
		}

		void OnMoveHandleHoverStarted(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			if (m_Dragging || m_Moving)
				return;

			m_WorkspaceUI.frontHighlightVisible = true;
		}

		void OnMoveHandleHoverEnded(BaseHandle handle, HandleEventData eventData = default(HandleEventData))
		{
			if (m_Dragging || m_Moving)
				return;

			m_WorkspaceUI.frontHighlightVisible = false;
		}

		IEnumerator AnimateResetSize()
		{
			var currentBoundsSize = contentBounds.size;
			var currentBoundsCenter = contentBounds.center;
			var targetBoundsSize = m_CustomStartingBounds != null ? m_CustomStartingBounds.Value : minBounds;
			var targetBoundsCenter = Vector3.zero;
			var smoothVelocitySize = Vector3.zero;
			var smoothVelocityCenter = Vector3.zero;
			var currentDuration = 0f;
			const float kTargetDuration = 0.75f;
			while (currentDuration < kTargetDuration)
			{
				currentDuration += Time.unscaledDeltaTime;
				currentBoundsCenter = U.Math.SmoothDamp(currentBoundsCenter, targetBoundsCenter, ref smoothVelocityCenter, kTargetDuration, Mathf.Infinity, Time.unscaledDeltaTime);
				currentBoundsSize = U.Math.SmoothDamp(currentBoundsSize, targetBoundsSize, ref smoothVelocitySize, kTargetDuration, Mathf.Infinity, Time.unscaledDeltaTime);
				contentBounds = new Bounds(currentBoundsCenter, currentBoundsSize);
				OnBoundsChanged();
				yield return null;
			}
		}
	}
}
