<?php

use App\Http\Controllers\CheckoutController;
use Illuminate\Support\Facades\Route;

Route::post('/storefront/orders', [CheckoutController::class, 'store']);
