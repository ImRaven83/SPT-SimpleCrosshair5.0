using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Unity.IL2CPP.Configuration;
using EFT;
using EFT.UI;
using Il2CppInterop.Runtime.Attributes;
using SimpleCrosshair.Config;
using SimpleCrosshair.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace SimpleCrosshair
{
    public enum EKeybindBehavior
    {
        DoNothing, PressToggles, ShowWhileHolding
    };

    public enum ECenterRadiusBehavior
    {
        DoNothing, HideInside, HideOutside
    };

    public class SimpleCrosshairComponent : MonoBehaviour
    {
        private readonly static string DefaultImagePath = Path.Combine(Plugin.Path, "crosshair.png");
        private readonly static HashSet<EPlayerState> PlayerStatesToHideWith = new HashSet<EPlayerState>()
        {
            EPlayerState.ProneMove,
            EPlayerState.Sprint,
            EPlayerState.BreachDoor,
        };

        // il2cpp wrappers are created as the declared type, so check the actual il2cpp type with TryCast
        private readonly static Func<Player.AbstractHandsController, bool>[] HandControllersToShowWith =
        {
            c => c.TryCast<Player.FirearmController>() != null,
            c => c.TryCast<Player.BaseKnifeController>() != null,
            c => c.TryCast<Player.BaseGrenadeHandsController>() != null,
        };

        private Image _crosshairImage;
        private bool _visible = true;
        private bool _laggingVisible = true;
        private Dictionary<string, bool> _reasonsToHide = new Dictionary<string, bool>();

        // general config
        private Vector2 _offset;
        private float _fadeInOutTime;
        private Color _color;

        // dynamic positioning config
        private bool _useDynamicPosition;
        private float _dynamicPositionSmoothTime;
        private float _dynamicPositionAimDistance;
        private ECenterRadiusBehavior _centerRadiusBehavior;
        private float _centerRadius;

        // keybind config
        private KeyboardShortcut _keyboardShortcut;
        private EKeybindBehavior _keybindBehavior;

        // crosshair images
        private string _currentImage;
        private Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();

        // cached variables
        private Player _cachedPlayer;
        private Player.FirearmController _cachedFirearmController;
        private Canvas _cachedCanvas;
        private Camera _cachedCamera;

        // il2cpp delegates for player events, kept so they can be unsubscribed again
        private Il2CppSystem.Action<Player.AbstractHandsController, Player.AbstractHandsController> _handsControllerChangedHandler;
        private Il2CppSystem.Action<bool> _senseChangedHandler;
        private Il2CppSystem.Action _possibleInteractionsChangedHandler;
        private Il2CppSystem.Action<bool> _aimingChangedHandler;

        // color fade tween state
        private bool _isFading;
        private Color _fadeFromColor;
        private Color _fadeToColor;
        private float _fadeElapsed;
        private bool _fadeToVisible;

        // dynamic position smoothing state
        private bool _hasTargetAnchorPosition;
        private Vector2 _targetAnchorPosition;
        private Vector2 _anchorPositionVelocity;

        // debug logging
        private float _nextDynamicDebugLogTime;

        public SimpleCrosshairComponent(IntPtr pointer) : base(pointer)
        {
        }

        [HideFromIl2Cpp]
        private static void DebugLog(string message)
        {
            if (Settings.DebugLogging != null && Settings.DebugLogging.Value)
            {
                Plugin.Logger.LogInfo($"[Debug] {message}");
            }
        }

        [HideFromIl2Cpp]
        public static GameObject AttachToBattleUIScreen(EftBattleUIScreen screen)
        {
            // setup container
            var go = new GameObject("SimpleCrosshair");
            go.AddComponent<RectTransform>();
            go.AddComponent<CanvasRenderer>();
            go.layer = screen.gameObject.layer;
            go.transform.SetParent(screen.transform);
            go.transform.localScale = Vector3.one;
            go.GetRectTransform().anchoredPosition = Vector2.zero;
            go.GetRectTransform().pivot = new Vector2(0.5f, 0.5f);
            go.GetRectTransform().anchorMin = new Vector2(0.5f, 0.5f);
            go.GetRectTransform().anchorMax = new Vector2(0.5f, 0.5f);

            // add our component
            var component = go.AddComponent<SimpleCrosshairComponent>();
            return go;
        }

        public void Awake()
        {
            // create our image component that will hold the crosshair loaded from config
            _crosshairImage = gameObject.AddComponent<Image>();
            _crosshairImage.type = Image.Type.Simple;

            // we may have missed register player, so if a player exists, go ahead and call it
            // TODO: investigate if this is needed or if the patch should be used instead
            if (GameUtils.IsGameActive && GameUtils.Player != null)
            {
                OnRegisterMainPlayer();
            }

            ReadConfig();
        }

        [HideFromIl2Cpp]
        private Sprite LoadSprite(string texturePath)
        {
            var texture = TextureUtils.LoadTexture2DFromPath(texturePath);
            var sprite = Sprite.Create(texture,
                                       new Rect(0f, 0f, texture.width, texture.height),
                                       new Vector2(texture.width / 2, texture.height / 2));

            // don't let unity unload the sprite on scene changes, we cache it
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return sprite;
        }

        public void Update()
        {
            // the register player patches aren't guaranteed to fire under il2cpp, so pick up the player if we missed it
            if (_cachedPlayer == null && GameUtils.IsGameActive && GameUtils.Player != null)
            {
                OnRegisterMainPlayer();
            }

            UpdateFade();
            UpdateDynamicPosition();

            // handle keyboard shortcuts
            if (_keybindBehavior == EKeybindBehavior.DoNothing || _keyboardShortcut.MainKey == KeyCode.None)
            {
                return;
            }

            switch (_keybindBehavior)
            {
                case EKeybindBehavior.PressToggles:
                    if (_keyboardShortcut.IsDown())
                    {
                        SetReasonToHide("disabled", !_reasonsToHide["disabled"]);
                    }
                    break;
                case EKeybindBehavior.ShowWhileHolding:
                    var isPressed = _keyboardShortcut.BetterIsPressed();
                    if (isPressed == _reasonsToHide["holdKeybind"])
                    {
                        SetReasonToHide("holdKeybind", !isPressed);
                    }
                    break;
                default:
                    break;
            }
        }

        public void FixedUpdate()
        {
            // handle dynamic position updates
            if (!_useDynamicPosition)
            {
                return;
            }

            try
            {
                CalculateDynamicAimPoint();
            }
            catch (Exception e)
            {
                // exceptions thrown into il2cpp can get swallowed, so make sure they show up in the log
                Plugin.Logger.LogError($"Dynamic crosshair position failed: {e}");
                _useDynamicPosition = false;
            }
        }

        [HideFromIl2Cpp]
        public void ReadConfig()
        {
            // general
            _color = Settings.Color.Value;
            _offset = new Vector2(Settings.OffsetX.Value, Settings.OffsetY.Value);
            _fadeInOutTime = Settings.FadeInOutTime.Value;
            gameObject.GetRectTransform().sizeDelta = Settings.Size.Value * Vector2.one;

            // reset color
            _crosshairImage.color = _color;

            // reset to static aim regardless, dynamic aim will update on fixed update
            ResetToStaticAimPoint();

            // dynamic positioning
            _useDynamicPosition = Settings.UseDynamicPosition.Value;
            _dynamicPositionSmoothTime = Settings.DynamicPositionSmoothTime.Value;
            _dynamicPositionAimDistance = Settings.DynamicPositionAimDistance.Value;
            _centerRadius = Settings.CenterRadius.Value;
            _centerRadiusBehavior = Settings.CenterRadiusBehavior.Value;

            // keybind shortcuts
            _keybindBehavior = Settings.KeybindBehavior.Value;
            _keyboardShortcut = Settings.KeyboardShortcut.Value;

            // load crosshair image from config if needed
            var imageName = Settings.ImageFileName.Value;
            var path = Path.Combine(Plugin.Path, imageName);

            // load sprite from path if not loaded before
            if (!_sprites.ContainsKey(path) && File.Exists(path))
            {
                _sprites[imageName] = LoadSprite(path);
            }

            // load sprite if not currently or if last loaded isn't this one
            if (_sprites.ContainsKey(imageName) && _currentImage != imageName)
            {
                _crosshairImage.sprite = _sprites[imageName];
                _currentImage = imageName;
            }

            // set initial reasons to hide and force visibility update
            _reasonsToHide["disabled"] = !Settings.Show.Value;
            _reasonsToHide["holdKeybind"] = _keybindBehavior == EKeybindBehavior.ShowWhileHolding;
            _reasonsToHide["centerRadius"] = false;
            SetVisibility(_visible, force: true);

            DebugLog($"ReadConfig: image={_currentImage} size={Settings.Size.Value} color={_color} show={Settings.Show.Value} " +
                     $"dynamic={_useDynamicPosition} centerRadius={_centerRadiusBehavior}/{_centerRadius} " +
                     $"keybind={_keyboardShortcut}/{_keybindBehavior} player={(_cachedPlayer != null)}");

            if (_useDynamicPosition)
            {
                CalculateDynamicAimPoint(false);
            }
        }

        [HideFromIl2Cpp]
        public void SetReasonToHide(string reason, bool shouldHide)
        {
            if (!_reasonsToHide.TryGetValue(reason, out var previous) || previous != shouldHide)
            {
                DebugLog($"Hide reason '{reason}' = {shouldHide}");
            }

            _reasonsToHide[reason] = shouldHide;
            SetVisibility(!_reasonsToHide.Any((pair) => pair.Value));
        }

        [HideFromIl2Cpp]
        private void SetVisibility(bool newVisible, bool shouldTween = true, bool force = false)
        {
            if (_visible == newVisible && !force)
            {
                return;
            }

            var toColor = new Color(_color.r, _color.g, _color.b, newVisible ? _color.a : 0);
            if (shouldTween && _fadeInOutTime > 0f)
            {
                // tween fade the crosshair in/out, see UpdateFade
                _isFading = true;
                _fadeFromColor = _crosshairImage.color;
                _fadeToColor = toColor;
                _fadeElapsed = 0f;
                _fadeToVisible = newVisible;
            }
            else
            {
                _isFading = false;
                _crosshairImage.color = toColor;
                _laggingVisible = newVisible;
            }

            _visible = newVisible;
        }

        [HideFromIl2Cpp]
        private void UpdateFade()
        {
            if (!_isFading)
            {
                return;
            }

            _fadeElapsed += Time.unscaledDeltaTime;
            var t = Mathf.Clamp01(_fadeElapsed / _fadeInOutTime);

            // ease out quad, same as the old DOTween default
            var eased = 1f - (1f - t) * (1f - t);
            _crosshairImage.color = Color.LerpUnclamped(_fadeFromColor, _fadeToColor, eased);

            if (t >= 1f)
            {
                _isFading = false;
                _laggingVisible = _fadeToVisible;
            }
        }

        [HideFromIl2Cpp]
        private void UpdateDynamicPosition()
        {
            if (!_hasTargetAnchorPosition)
            {
                return;
            }

            var rectTransform = _crosshairImage.GetRectTransform();
            if (_dynamicPositionSmoothTime <= 0f)
            {
                rectTransform.anchoredPosition = _targetAnchorPosition;
                _hasTargetAnchorPosition = false;
                return;
            }

            rectTransform.anchoredPosition = Vector2.SmoothDamp(rectTransform.anchoredPosition,
                                                                _targetAnchorPosition,
                                                                ref _anchorPositionVelocity,
                                                                _dynamicPositionSmoothTime,
                                                                float.PositiveInfinity,
                                                                Time.unscaledDeltaTime);
        }

        [HideFromIl2Cpp]
        private void SetAnchorPositionImmediate(Vector2 position)
        {
            _hasTargetAnchorPosition = false;
            _anchorPositionVelocity = Vector2.zero;
            _crosshairImage.GetRectTransform().anchoredPosition = position;
        }

        [HideFromIl2Cpp]
        private void CalculateDynamicAimPoint(bool shouldTween = true)
        {
            if (_cachedPlayer == null)
            {
                return;
            }

            // the fireport can be missing while a weapon is being swapped, fall back to the camera then
            var fireport = _cachedFirearmController != null ? _cachedFirearmController.CurrentFireport : null;
            var useFireport = fireport != null;

            var rayBegin = useFireport
                ? fireport.position
                : _cachedPlayer.CameraPosition.position;

            var rayDirection = useFireport
                ? _cachedFirearmController.WeaponDirection
                : _cachedPlayer.LookDirection;

            // do raycast, if it hits, use that as aim point, if not, use end of ray
            var ray = new Ray(rayBegin, rayDirection);
            var rayEnd = rayBegin + rayDirection.normalized * _dynamicPositionAimDistance;
            var didRayHit = Physics.Raycast(
                ray, out RaycastHit rayHit, _dynamicPositionAimDistance, LayersMaskController.HighPolyWithTerrainMask);

            var worldAimPoint = didRayHit ? rayHit.point : rayEnd;
            var screenAimPoint = GetCanvasScreenPosition(worldAimPoint);

            if (Time.unscaledTime >= _nextDynamicDebugLogTime)
            {
                _nextDynamicDebugLogTime = Time.unscaledTime + 1f;
                DebugLog($"Dynamic aim: firearm={(_cachedFirearmController != null)} hit={didRayHit} " +
                         $"screenAimPoint={screenAimPoint} canvas={(_cachedCanvas != null ? _cachedCanvas.renderMode.ToString() : "none")} " +
                         $"camera={(_cachedCamera != null ? _cachedCamera.name : "none")}");
            }

            // move the anchor position of the cursor to the aim point
            // check visibility status before tweening so that if the player can't see it, it doesn't tween
            if (shouldTween && (_visible || _laggingVisible))
            {
                // smoothed towards in UpdateDynamicPosition
                _targetAnchorPosition = screenAimPoint + _offset;
                _hasTargetAnchorPosition = true;
            }
            else
            {
                SetAnchorPositionImmediate(screenAimPoint + _offset);
            }

            // lastly, do center radius handling
            if (_centerRadiusBehavior == ECenterRadiusBehavior.DoNothing)
            {
                return;
            }

            var shouldHide = false;
            var currentRadius = screenAimPoint.magnitude;
            if (_centerRadiusBehavior == ECenterRadiusBehavior.HideInside &&
                currentRadius < _centerRadius)
            {
                shouldHide = true;
            }
            else if(_centerRadiusBehavior == ECenterRadiusBehavior.HideOutside &&
                    currentRadius > _centerRadius)
            {
                shouldHide = true;
            }

            // only call if different than before, since we don't want to spam it, since called on fixed update
            if (shouldHide != _reasonsToHide["centerRadius"])
            {
                SetReasonToHide("centerRadius", shouldHide);
            }
        }

        [HideFromIl2Cpp]
        private void ResetToStaticAimPoint()
        {
            SetAnchorPositionImmediate(_offset);
        }

        [HideFromIl2Cpp]
        private static Camera FindPlayerCamera()
        {
            var camera = Camera.main;
            if (camera != null)
            {
                return camera;
            }

            // fall back to the first person camera by name, in case it isn't tagged as the main camera
            var cameraGO = GameObject.Find("FPS Camera");
            return cameraGO != null ? cameraGO.GetComponent<Camera>() : null;
        }

        [HideFromIl2Cpp]
        private Vector2 GetCanvasScreenPosition(Vector3 worldPoint)
        {
            // the camera may not exist yet when the player is registered during spawn, so refresh lazily
            if (_cachedCamera == null || !_cachedCamera.isActiveAndEnabled)
            {
                _cachedCamera = FindPlayerCamera();
            }

            if (_cachedCanvas == null)
            {
                _cachedCanvas = gameObject.GetComponentInParent<Canvas>();
            }

            if (_cachedCamera == null || _cachedCanvas == null)
            {
                return Vector2.zero;
            }

            // project into screen space, mirroring points behind the camera so they end up off screen
            var screenPoint = _cachedCamera.WorldToScreenPoint(worldPoint);
            if (screenPoint.z < 0f)
            {
                screenPoint = new Vector3(Screen.width - screenPoint.x, Screen.height - screenPoint.y, 0f);
            }

            // convert into the local space of our parent, which is what anchoredPosition is relative to,
            // this works regardless of canvas render mode, scaling or how the parent is sized
            var parentRect = transform.parent != null ? transform.parent.GetRectTransform() : null;
            if (parentRect != null)
            {
                var uiCamera = _cachedCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : _cachedCanvas.worldCamera;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRect, new Vector2(screenPoint.x, screenPoint.y),
                                                                            uiCamera, out var localPoint))
                {
                    // our anchors are at the center of the parent rect
                    return localPoint - parentRect.rect.center;
                }
            }

            // fallback, scale the viewport point by the canvas size
            var canvasRect = _cachedCanvas.GetRectTransform();
            var viewportPoint = _cachedCamera.WorldToViewportPoint(worldPoint);
            return new Vector2(
                (viewportPoint.x - 0.5f) * canvasRect.rect.width,
                (viewportPoint.y - 0.5f) * canvasRect.rect.height);
        }

        [HideFromIl2Cpp]
        internal void OnRegisterMainPlayer()
        {
            if (!GameUtils.IsGameActive) return;
            var player = GameUtils.Player;
            if (player == null) return;

            // if we were already registered (e.g. from Awake and the patch), don't double subscribe
            if (_cachedPlayer != null)
            {
                UnsubscribeFromPlayer(_cachedPlayer);
            }

            _cachedPlayer = player;
            _cachedCamera = FindPlayerCamera();
            _cachedCanvas = gameObject.GetComponentInParent<Canvas>();

            // register events on player, il2cpp events need il2cpp delegates
            _handsControllerChangedHandler ??= (Action<Player.AbstractHandsController, Player.AbstractHandsController>)OnHandControllerChanged;
            _senseChangedHandler ??= (Action<bool>)OnSenseChanged;
            _possibleInteractionsChangedHandler ??= (Action)PossibleInteractionsChanged;

            player.add_OnHandsControllerChanged(_handsControllerChangedHandler);
            player.add_OnSenseChanged(_senseChangedHandler);
            player.add_PossibleInteractionsChanged(_possibleInteractionsChangedHandler);

            // we may have missed initial event, so call it now
            OnHandControllerChanged(null, player.HandsController);

            // clear all reasons to hide, since we're resetting the player
            _reasonsToHide.Clear();

            Plugin.Logger.LogInfo("Registered main player with crosshair");

            ReadConfig();
        }

        [HideFromIl2Cpp]
        internal void OnUnregisterMainPlayer()
        {
            // use unity's null check, the player may have already been destroyed
            if (_cachedPlayer == null) return;

            UnsubscribeFromPlayer(_cachedPlayer);
            _cachedPlayer = null;
            _cachedFirearmController = null;
        }

        [HideFromIl2Cpp]
        private void UnsubscribeFromPlayer(Player player)
        {
            // unregister events on player
            if (_handsControllerChangedHandler != null)
            {
                player.remove_OnHandsControllerChanged(_handsControllerChangedHandler);
            }

            if (_senseChangedHandler != null)
            {
                player.remove_OnSenseChanged(_senseChangedHandler);
            }

            if (_possibleInteractionsChangedHandler != null)
            {
                player.remove_PossibleInteractionsChanged(_possibleInteractionsChangedHandler);
            }

            // unregister from hands controller if there is one
            OnHandControllerChanged(player.HandsController, null);
        }

        [HideFromIl2Cpp]
        private void OnHandControllerChanged(Player.AbstractHandsController oldController,
                                             Player.AbstractHandsController newController)
        {
            if (oldController != null && _aimingChangedHandler != null)
            {
                oldController.remove_OnAimingChanged(_aimingChangedHandler);
            }

            if (newController == null)
            {
                return;
            }

            DebugLog($"Hands controller changed to {newController.GetIl2CppType().FullName}");

            _aimingChangedHandler ??= (Action<bool>)OnAimingChanged;
            newController.add_OnAimingChanged(_aimingChangedHandler);

            // check if controller is one that we want to show a crosshair for
            SetReasonToHide("handController", !HandControllersToShowWith.Any(isController => isController(newController)));

            _cachedFirearmController = newController.TryCast<Player.FirearmController>();
        }

        [HideFromIl2Cpp]
        private void OnAimingChanged(bool isAiming)
        {
            SetReasonToHide("aiming", isAiming);
        }

        [HideFromIl2Cpp]
        private void OnSenseChanged(bool sensingItem)
        {
            SetReasonToHide("sensing", sensingItem);
        }

        [HideFromIl2Cpp]
        internal void OnMovementStateChanged(EPlayerState state)
        {
            SetReasonToHide("movementState", PlayerStatesToHideWith.Contains(state));
        }

        [HideFromIl2Cpp]
        private void PossibleInteractionsChanged()
        {
            SetReasonToHide("interactableObject", _cachedPlayer != null && _cachedPlayer.InteractableObject != null);
        }

        public void OnDestroy()
        {
            OnUnregisterMainPlayer();
        }
    }
}
