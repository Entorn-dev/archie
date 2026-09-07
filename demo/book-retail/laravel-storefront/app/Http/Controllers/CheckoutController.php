<?php

namespace App\Http\Controllers;

use Illuminate\View\View;

final class CheckoutController
{
    public function store(): View
    {
        return view('checkout');
    }
}
