LOCAL_PATH := $(call my-dir)

include $(CLEAR_VARS)
LOCAL_MODULE := boluoimp
LOCAL_SRC_FILES := boluo_bridge_push.c
LOCAL_LDLIBS := -llog -ldl
include $(BUILD_SHARED_LIBRARY)
