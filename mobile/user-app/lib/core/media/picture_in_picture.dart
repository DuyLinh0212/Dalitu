import 'dart:async';

import 'package:flutter/foundation.dart'
    show defaultTargetPlatform, TargetPlatform;
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

/// Uses the existing Flutter surface; entering PiP never opens another player.
class AppPictureInPicture {
  static const channel = MethodChannel('hutube/picture_in_picture');
  static final _bindings =
      <({Object owner, Future<bool> Function() prepare})>[];
  static Object? get _owner => _bindings.lastOrNull?.owner;
  // True while preparing too: overlays must disappear before Android captures.
  static final active = ValueNotifier<bool>(false);
  static void bind(Object owner, Future<bool> Function() prepare) {
    _bindings.removeWhere((binding) => identical(binding.owner, owner));
    _bindings.add((owner: owner, prepare: prepare));
    _installHandler();
  }

  static void bindFallback(Object owner, Future<bool> Function() prepare) {
    _bindings.removeWhere((binding) => identical(binding.owner, owner));
    _bindings.insert(0, (owner: owner, prepare: prepare));
    _installHandler();
  }

  static void _installHandler() {
    if (_bindings.isEmpty) {
      channel.setMethodCallHandler(null);
      return;
    }
    channel.setMethodCallHandler((call) async {
      if (call.method == 'modeChanged') {
        active.value = call.arguments == true;
        return null;
      }
      if (call.method == 'prepare') {
        final binding = _bindings.lastOrNull;
        if (binding == null) return false;
        active.value = true;
        try {
          final ready = await binding.prepare();
          if (!identical(_owner, binding.owner)) return false;
          if (!ready) active.value = false;
          return ready;
        } catch (_) {
          if (identical(_owner, binding.owner)) active.value = false;
          return false;
        }
      }
      return false;
    });
  }

  static void unbind(Object owner) {
    final wasCurrent = identical(_owner, owner);
    _bindings.removeWhere((binding) => identical(binding.owner, owner));
    if (wasCurrent) active.value = false;
    _installHandler();
  }

  static Future<bool> available() async {
    try {
      return await channel.invokeMethod<bool>('available') ?? false;
    } catch (_) {
      return false;
    }
  }

  /// Capture the laid-out video, after PiP has hidden the surrounding app UI.
  /// Android expects physical window coordinates for its source rectangle.
  static Future<bool> prepareSurface(GlobalKey surfaceKey) async {
    try {
      WidgetsBinding.instance.scheduleFrame();
      await WidgetsBinding.instance.endOfFrame.timeout(
        const Duration(milliseconds: 700),
      );
      final context = surfaceKey.currentContext;
      final box = context?.findRenderObject();
      if (context == null ||
          !context.mounted ||
          box is! RenderBox ||
          !box.hasSize ||
          box.size.isEmpty) {
        return false;
      }
      final ratio = View.of(context).devicePixelRatio;
      final rect = box.localToGlobal(Offset.zero) & box.size;
      await _setSourceRect(rect, ratio);
      return true;
    } catch (_) {
      return false;
    }
  }

  static Future<void> _setSourceRect(Rect rect, double ratio) =>
      channel.invokeMethod<void>('setSourceRect', {
        'left': (rect.left * ratio).round(),
        'top': (rect.top * ratio).round(),
        'right': (rect.right * ratio).round(),
        'bottom': (rect.bottom * ratio).round(),
      });

  static Future<bool> enter() async {
    try {
      final entered = await channel.invokeMethod<bool>('enter') ?? false;
      if (!entered) active.value = false;
      return entered;
    } catch (_) {
      active.value = false;
      return false;
    }
  }

  static Future<void> configure({required bool automatic}) async {
    try {
      await channel.invokeMethod<void>('configure', {'automatic': automatic});
    } catch (_) {
      /* Unsupported platforms leave playback in the app. */
    }
  }
}

/// Keep Android's automatic PiP source ready while Flutter is still visible.
/// Leaving the app must not depend on rendering another Flutter frame.
class PipVideoSurface extends SingleChildRenderObjectWidget {
  const PipVideoSurface({super.key, required super.child});

  @override
  RenderObject createRenderObject(BuildContext context) => _PipVideoSurface(
    View.of(context).devicePixelRatio,
    defaultTargetPlatform == TargetPlatform.android &&
        ModalRoute.of(context)?.isCurrent != false,
  );

  @override
  void updateRenderObject(BuildContext context, RenderObject renderObject) {
    final surface = renderObject as _PipVideoSurface;
    surface
      ..ratio = View.of(context).devicePixelRatio
      ..enabled =
          defaultTargetPlatform == TargetPlatform.android &&
          ModalRoute.of(context)?.isCurrent != false
      ..markNeedsPaint();
  }
}

class _PipVideoSurface extends RenderProxyBox {
  _PipVideoSurface(this.ratio, this.enabled);
  double ratio;
  bool enabled;
  Rect? _lastRect;
  double? _lastRatio;
  bool _scheduled = false;

  void _report() {
    if (!enabled || _scheduled) return;
    _scheduled = true;
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _scheduled = false;
      if (!attached || !enabled || !hasSize || size.isEmpty) return;
      final rect = localToGlobal(Offset.zero) & size;
      if (rect == _lastRect && ratio == _lastRatio) return;
      _lastRect = rect;
      _lastRatio = ratio;
      unawaited(
        AppPictureInPicture._setSourceRect(
          rect,
          ratio,
        ).catchError((Object _) {}),
      );
    });
  }

  @override
  void performLayout() {
    super.performLayout();
    _report();
  }

  @override
  void paint(PaintingContext context, Offset offset) {
    super.paint(context, offset);
    _report();
  }
}
