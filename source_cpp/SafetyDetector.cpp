#include "SafetyDetector.h"
#include <iostream>

SafetyDetector::SafetyDetector() {
    string base = "C:/Users/dbsdm/Desktop/cppopencv/opencv1/";
    try {
        net = readNetFromCaffe(base + "deploy.prototxt", base + "mobilenet_iter_73000.caffemodel");
        helmetNet = readNetFromDarknet(base + "yolov3-tiny.cfg", base + "yolov3-tiny.weights");
    }
    catch (...) { cout << "❌ 모델 로드 실패" << endl; }
}

Rect SafetyDetector::scaleRect(const Rect& oriBox, int oriW, int oriH) {
    float sx = 800.0f / oriW; float sy = 600.0f / oriH;
    return Rect((int)(oriBox.x * sx), (int)(oriBox.y * sy), (int)(oriBox.width * sx), (int)(oriBox.height * sy));
}

Rect SafetyDetector::enlargeRectFixed(const Rect& in, int pw, int ph, int fc, int fr) {
    Rect out = in; out.x -= pw; out.y -= ph; out.width += (pw * 2); out.height += (ph * 2);
    return out & Rect(0, 0, fc, fr);
}

// ⭐ [감도 상향] 헬멧 색상 탐지 (노란색 범위 확장)
bool SafetyDetector::detectHelmetByColor(const Mat& head, Mat& mask, float& conf) {
    if (head.empty()) return false;
    Mat hsv; cvtColor(head, hsv, COLOR_BGR2HSV);
    Mat mWhite, mYellow;
    // 하얀색 (기존 유지)
    inRange(hsv, Scalar(0, 0, 180), Scalar(180, 40, 255), mWhite);
    // ⭐ 노란색 범위 확장 (채도와 명도 하한선을 낮춤)
    inRange(hsv, Scalar(10, 60, 80), Scalar(40, 255, 255), mYellow);
    mask = mWhite | mYellow;
    conf = (float)countNonZero(mask) / (head.rows * head.cols);
    return conf > 0.04; // 임계값 4%로 완화
}

bool SafetyDetector::detectHelmetByShape(const Mat& head, vector<Vec3f>& circles, float& conf) {
    if (head.empty()) return false;
    Mat gray; cvtColor(head, gray, COLOR_BGR2GRAY); equalizeHist(gray, gray);
    GaussianBlur(gray, gray, Size(5, 5), 1.2);
    HoughCircles(gray, circles, HOUGH_GRADIENT, 1, gray.rows / 4, 100, 22, gray.rows / 12, (int)(gray.rows * 0.5));
    if (!circles.empty()) {
        conf = (circles[0][2] * 2) / (float)min(head.cols, head.rows);
        return (circles[0][0] > head.cols * 0.1 && circles[0][0] < head.cols * 0.9 && conf > 0.3);
    }
    return false;
}

bool SafetyDetector::detectHelmetByEdge(const Mat& head, Mat& edge, float& conf) {
    if (head.empty()) return false;
    Mat gray; cvtColor(head, gray, COLOR_BGR2GRAY); Canny(gray, edge, 50, 150);
    conf = (float)countNonZero(edge) / (head.rows * head.cols);
    return conf > 0.04;
}