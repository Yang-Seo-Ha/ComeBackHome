#include "SafetyDetector.h"
#include <opencv2/dnn.hpp>

using namespace cv;
using namespace cv::dnn;
using namespace std;

void LadderDetector::runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) {
    if (frame.empty()) return;

    // 1. 사람 탐지 (MobileNet SSD)
    Mat blob = blobFromImage(frame, 0.007843, Size(300, 300), Scalar(127.5, 127.5, 127.5));
    net.setInput(blob);
    Mat prob = net.forward();
    Mat detM(prob.size[2], prob.size[3], CV_32F, prob.ptr<float>());

    Rect rawBox;
    float maxC = 0;

    for (int i = 0; i < detM.rows; i++) {
        if ((int)detM.at<float>(i, 1) == 15 && detM.at<float>(i, 2) > cfg.confidenceThreshold) {
            int x1 = (int)(detM.at<float>(i, 3) * frame.cols);
            int y1 = (int)(detM.at<float>(i, 4) * frame.rows);
            int x2 = (int)(detM.at<float>(i, 5) * frame.cols);
            int y2 = (int)(detM.at<float>(i, 6) * frame.rows);

            if (detM.at<float>(i, 2) > maxC) {
                maxC = detM.at<float>(i, 2);
                rawBox = Rect(x1, y1, x2 - x1, y2 - y1);
            }
        }
    }

    // 2. 추적 유지 로직
    if (maxC > 0) { lastRect = rawBox; isFirstDetection = false; }
    else if (!isFirstDetection) rawBox = lastRect;
    else return;

    // 3. 헬멧 검사 영역(ROI) 설정
    Rect pBox = enlargeRectFixed(rawBox, 25, 60, frame.cols, frame.rows);
    Rect hROI = Rect(pBox.x, pBox.y, pBox.width, (int)(pBox.height * 0.28)) & Rect(0, 0, frame.cols, frame.rows);

    bool helmetOk = false;
    if (hROI.width > 20) {
        Mat head = frame(hROI), cMask;
        float cC = 0, sC = 0;
        vector<Vec3f> circles;

        // 원본 로직대로 색상 및 형태 검사
        bool cR = detectHelmetByColor(head, cMask, cC);
        bool sR = detectHelmetByShape(head, circles, sC);

        helmetOk = (cR || sR);
    }

    // ✅ 시각화 코드 삭제 완료: 오직 데이터만 추가하여 C#으로 전송
    // label: 0(사람), isSafe: helmetOk ? 1(안전) : 0(위험)
    this->addResult(0, helmetOk ? 1 : 0, pBox);
}