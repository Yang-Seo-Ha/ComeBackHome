#include "SafetyDetector.h"
#include <iostream>

SafetyDetector::SafetyDetector() {
    // 상대 경로로 변경 (실행파일 위치의 models_cpp 폴더 기준)
    string base = "models_cpp/";
    try {
        net = readNetFromCaffe(base + "deploy.prototxt", base + "mobilenet_iter_73000.caffemodel");
        helmetNet = readNetFromDarknet(base + "yolov3-tiny.cfg", base + "yolov3-tiny.weights");
    }
    catch (...) { cout << "❌ 모델 로드 실패: models_cpp 폴더에 모델 파일이 있는지 확인하세요." << endl; }
}

Rect SafetyDetector::scaleRect(const Rect& oriBox, int oriW, int oriH) {
    float sx = 800.0f / oriW; float sy = 600.0f / oriH;
    return Rect((int)(oriBox.x * sx), (int)(oriBox.y * sy), (int)(oriBox.width * sx), (int)(oriBox.height * sy));
}

Rect SafetyDetector::enlargeRectFixed(const Rect& in, int pw, int ph, int fc, int fr) {
    Rect out = in; out.x -= pw; out.y -= ph; out.width += (pw * 2); out.height += (ph * 2);
    return out & Rect(0, 0, fc, fr);
}

bool SafetyDetector::detectHelmetByColor(const Mat& head, Mat& mask, float& conf) {
    if (head.empty()) return false;
    Mat hsv; cvtColor(head, hsv, COLOR_BGR2HSV);
    Mat mWhite, mYellow;
    inRange(hsv, Scalar(0, 0, 180), Scalar(180, 40, 255), mWhite);
    inRange(hsv, Scalar(10, 60, 80), Scalar(40, 255, 255), mYellow);
    mask = mWhite | mYellow;
    conf = (float)countNonZero(mask) / (head.rows * head.cols);
    return conf > 0.04;
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