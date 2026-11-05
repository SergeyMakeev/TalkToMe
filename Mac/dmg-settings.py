"""Finder layout for the TalkToMe drag-to-install disk image."""

files = [defines["app"]]
symlinks = {"Applications": "/Applications"}
format = "UDZO"
background = "builtin-arrow"
default_view = "icon-view"
window_rect = ((100, 100), (640, 280))
icon_locations = {"TalkToMe.app": (140, 120), "Applications": (500, 120)}
icon_size = 128
text_size = 16
